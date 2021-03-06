﻿//
// Author:
//   Aaron Bockover <abock@xamarin.com>
//
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xamarin.Interactive.CodeAnalysis;
using Xamarin.Interactive.CodeAnalysis.Events;
using Xamarin.Interactive.Core;
using Xamarin.Interactive.Logging;
using Xamarin.Interactive.Session;
using Xamarin.Interactive.Workbook.Models;
using Xamarin.Interactive.Representations;

using static System.Console;

namespace Xamarin.Interactive.Client.Console
{
    static class Entry
    {
        static LanguageDescription language;
        static CodeCellId lastCodeCellId;
        static CodeCellEvaluationStatus lastCellEvaluationStatus;

        /// <summary>
        /// Hosts an interactive REPL against a supported Workbooks target platform.
        /// This is analogous to 'csharp' or 'csi' or any other REPL on the planet.
        /// </summary>
        static async Task<int> ReplPlayerMain ()
        {
            // As an exercise to the reader, this puppy should take an optional
            // workbook flavor ID to know what platform you want to REPL and find
            // it in the list of installed and available ones...
            // For now we'll just pick the first available option 😺
            var workbookTarget = WorkbookAppInstallation.All.FirstOrDefault ();
            if (workbookTarget == null) {
                RenderError ("No workbook target platforms could be found.");
                return 1;
            }

            // We do not currently expose a list of available language descriptions
            // for the given build/installation, but if we did, this is when
            // you'd want to pick one. Just assume 'csharp' for now. Stay tuned.
            language = "csharp";

            // A session description combines just enough info for the entire
            // EvaluationService to get itself in order to do your bidding.
            var sessionDescription = new InteractiveSessionDescription (
                language,
                workbookTarget.Id,
                new EvaluationEnvironment (Environment.CurrentDirectory));

            // Now create and get ready to deal with the session; a more complete
            // client should handle more than just OnNext from the observer.
            var session = InteractiveSession.CreateWorkbookSession ();
            session.Events.Subscribe (new Observer<InteractiveSessionEvent> (OnSessionEvent));

            // And initialize it with all of our prerequisites...
            // Status events raised within this method will be posted to the
            // observable above ("starting agent", "initializing workspace", etc).
            await session.InitializeAsync (sessionDescription);

            // At this point we have the following in order, ready to serve:
            //
            //   1. a connected agent ready to execute code
            //   2. a workspace that can perform compliation, intellisense, etc
            //   3. an evaluation service that is ready to deal with (1) and (2)
            //
            // It's at this point that a full UI would allow the user to actually
            // run code. This is the "User Experience main()"...
            //
            // This is the REPL you're looking for...
            while (true) {
                // append a new cell (no arguments here imply append)
                var cellId = await session.EvaluationService.InsertCodeCellAsync ();

                // render the initial/top-level prompt
                WriteReplPrompt ();

                while (true) {
                    var deltaBuffer = ReadLine ();
                    var existingBuffer = await session.EvaluationService.GetCodeCellBufferAsync (cellId);

                    await session.EvaluationService.UpdateCodeCellAsync (
                        cellId,
                        existingBuffer.Value + deltaBuffer);

                    if (session.WorkspaceService.IsCellComplete (cellId))
                        break;

                    WriteReplPrompt (secondaryPrompt: true);
                }

                await session.EvaluationService.EvaluateAsync (cellId);
                await EvaluationServiceRaceBug ();
            }
        }

        /// <summary>
        /// Provides very basic workbook parsing and shunting of code cells
        /// into the evaluation service. Does not display non-code-cell contents
        /// but does evaluate a workbook from top-to-bottom. Restores nuget
        /// packages from the workbook's manifest.
        /// </summary>
        static async Task<int> WorkbookPlayerMain (string workbookPath)
        {
            var path = new FilePath (workbookPath);
            if (!path.FileExists) {
                Error.WriteLine ($"File does not exist: {path}");
                return 1;
            }

            // load the workbook file
            var workbook = new WorkbookPackage (path);
            await workbook.Open (
                quarantineInfo => Task.FromResult (true),
                path);

            // create and get ready to deal with the session; a more complete
            // client should handle more than just OnNext from the observer.
            var session = InteractiveSession.CreateWorkbookSession ();
            session.Events.Subscribe (new Observer<InteractiveSessionEvent> (OnSessionEvent));

            #pragma warning disable 0618
            // TODO: WorkbookDocumentManifest needs to eliminate AgentType like we've done on web
            // to avoid having to use the the flavor mapping in AgentIdentity.
            var targetPlatformIdentifier = AgentIdentity.GetFlavorId (workbook.PlatformTargets [0]);
            #pragma warning restore 0618

            // initialize the session based on manifest metadata from the workbook file
            language = workbook.GetLanguageDescriptions ().First ();
            await session.InitializeAsync (new InteractiveSessionDescription (
                language,
                targetPlatformIdentifier,
                new EvaluationEnvironment (Environment.CurrentDirectory)));

            // restore NuGet packages
            await session.PackageManagerService.RestoreAsync (
                workbook.Pages.SelectMany (page => page.Packages));

            // insert and evaluate cells in the workbook
            foreach (var cell in workbook.IndexPage.Contents.OfType<CodeCell> ()) {
                var buffer = cell.CodeAnalysisBuffer.Value;

                lastCodeCellId = await session.EvaluationService.InsertCodeCellAsync (
                    buffer,
                    lastCodeCellId);

                WriteReplPrompt ();
                WriteLine (buffer);

                await session.EvaluationService.EvaluateAsync (lastCodeCellId);
                await EvaluationServiceRaceBug ();

                if (lastCellEvaluationStatus != CodeCellEvaluationStatus.Success)
                    break;
            }

            return 0;
        }

        #region Respond Nicely To Session Events

        static void OnSessionEvent (InteractiveSessionEvent evnt)
        {
            switch (evnt.Kind) {
            case InteractiveSessionEventKind.Evaluation:
                OnCodeCellEvent ((ICodeCellEvent)evnt.Data);
                break;
            default:
                ForegroundColor = ConsoleColor.Cyan;
                WriteLine (evnt.Kind);
                ResetColor ();
                break;
            }
        }

        static void OnCodeCellEvent (ICodeCellEvent codeCellEvent)
        {
            // NOTE: events may post to cells "out of order" from evaluation in
            // special cases such as reactive/IObservable integrations. This is
            // not handled at all in this simple console client since we just
            // append to stdout. Because of this, ignore "out of order" cell
            // events entirely. A real UI would render them in the correct place.
            if (lastCodeCellId != default && codeCellEvent.CodeCellId != lastCodeCellId)
                return;

            switch (codeCellEvent) {
            // a full UI would set cell state to show a spinner and
            // enable a button to abort the running evaluation here
            case CodeCellEvaluationStartedEvent _:
                break;
                // and would then hide the spinner and button here
            case CodeCellEvaluationFinishedEvent finishedEvent:
                lastCellEvaluationStatus = finishedEvent.Status;

                switch (finishedEvent.Status) {
                case CodeCellEvaluationStatus.Disconnected:
                    RenderError ("Agent was disconnected while evaluating cell");
                    break;
                case CodeCellEvaluationStatus.Interrupted:
                    RenderError ("Evaluation was aborted");
                    break;
                case CodeCellEvaluationStatus.EvaluationException:
                    RenderError ("An exception was thrown while evaluating cell");
                    break;
                }

                foreach (var diagnostic in finishedEvent.Diagnostics)
                    RenderDiagnostic (diagnostic);

                break;
                // and would render console output and results on the cell itself
                // instead of just appending to the screen (see "out of order" note)
            case CapturedOutputSegment output:
                RenderOutput (output);
                break;
            case CodeCellResultEvent result:
                RenderResult (result);
                break;
            }
        }

        #endregion

        #region Amazing UI

        static void WriteReplPrompt (bool secondaryPrompt = false)
        {
            ForegroundColor = ConsoleColor.DarkYellow;
            var prompt = language.Name;
            if (secondaryPrompt)
                prompt = string.Empty.PadLeft (prompt.Length);
            Write ($"{prompt}> ");
            ResetColor ();
        }

        static void RenderOutput (CapturedOutputSegment output)
        {
            switch (output.FileDescriptor) {
            case 1:
                ForegroundColor = ConsoleColor.Gray;
                break;
            case 2:
                ForegroundColor = ConsoleColor.Red;
                break;
            }

            Write (output.Value);

            ResetColor ();
        }

        static void RenderResult (CodeCellResultEvent result)
        {
            ForegroundColor = ConsoleColor.Magenta;
            Write (result.Type.Name);
            Write (": ");
            ResetColor ();

            // A full client would implement real result rendering and interaction,
            // but console client only cares about showing the ToString representation
            // right now. We will always serialize a ReflectionInteractiveObject as
            // a representation of a result which always contains the result of calling
            // .ToString on that value. Find it and display that. If that's not available,
            // then the result was null or something unexpected happened in eval.
            WriteLine (
                result
                    .ValueRepresentations
                    .OfType<ReflectionInteractiveObject> ()
                    .FirstOrDefault ()
                    ?.ToStringRepresentation ??
                        result.ValueRepresentations.FirstOrDefault () ??
                            "null");
        }

        static void RenderError (string message)
        {
            ForegroundColor = ConsoleColor.Red;
            Write ("Error: ");
            ResetColor ();
            WriteLine (message);
        }

        static void RenderDiagnostic (InteractiveDiagnostic diagnostic)
        {
            switch (diagnostic.Severity) {
            case Microsoft.CodeAnalysis.DiagnosticSeverity.Warning:
                ForegroundColor = ConsoleColor.DarkYellow;
                Write ($"warning ({diagnostic.Id}): ");
                break;
            case Microsoft.CodeAnalysis.DiagnosticSeverity.Error:
                ForegroundColor = ConsoleColor.Red;
                Write ($"error ({diagnostic.Id}): ");
                break;
            default:
                return;
            }

            ResetColor ();

            var (line, column) = diagnostic.Span;
            WriteLine ($"({line},{column}): {diagnostic.Message}");
        }

        #endregion

        #region Boring Entry/Hosting

        static int Main (string [] args)
        {
            var runContext = new SingleThreadSynchronizationContext ();
            SynchronizationContext.SetSynchronizationContext (runContext);

            var mainTask = MainAsync (args);

            mainTask.ContinueWith (
                task => runContext.Complete (),
                TaskScheduler.Default);

            runContext.RunOnCurrentThread ();

            return mainTask.GetAwaiter ().GetResult ();
        }

        static async Task<int> MainAsync (string [] args)
        {
            // set up the very basic global services/environment
            var clientApp = new ConsoleClientApp ();
            clientApp.Initialize (
                logProvider: new LogProvider (LogLevel.Info, null));

            if (args.Length > 0)
                await WorkbookPlayerMain (args [0]);
            else
                await ReplPlayerMain ();

            // Nevermind this... it'll get fixed!
            await Task.Delay (Timeout.Infinite);
            return 0;
        }

        /// <summary>
        /// There is clearly an issue in EvaluationService where the events
        /// observable will post evaluation results _after_ the evalaution
        /// has happened. This is in theory _correct_ behavior, since evaluation
        /// results may be posted at any time, regardless of whether or not
        /// a cell is evaluating. However, for the first result of a cell,
        /// the call should await it. This is not an issue in the other
        /// UX scenarios (desktop and web). Need to look into this...
        /// </summary>
        static Task EvaluationServiceRaceBug ()
         => Task.Delay (250);

        #endregion
    }
}