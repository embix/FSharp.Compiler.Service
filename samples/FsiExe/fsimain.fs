//----------------------------------------------------------------------------
// This sample checks that the standard fsi.exe can be built when using the compiler API
// through appropriate configuration parameters.
//----------------------------------------------------------------------------

//----------------------------------------------------------------------------
// Copyright (c) 2002-2012 Microsoft Corporation. 
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.html file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
//----------------------------------------------------------------------------


module internal Sample.Microsoft.FSharp.Compiler.Interactive.Main

open System
open System.Globalization
open System.IO
open System.Reflection
open System.Threading
open System.Windows.Forms

open Microsoft.FSharp.Compiler.Interactive.Shell
open Microsoft.FSharp.Compiler.Interactive
open Microsoft.FSharp.Compiler

#nowarn "55"

[<assembly: System.Runtime.InteropServices.ComVisible(false)>]
[<assembly: System.CLSCompliant(true)>]  
do()

/// Set the current ui culture for the current thread.
let internal SetCurrentUICultureForThread (lcid : int option) =
    match lcid with
    | Some n -> Thread.CurrentThread.CurrentUICulture <- new CultureInfo(n)
    | None -> ()

///Use a dummy to access protected member
type internal DummyForm() = 
    inherit Form() 
    member x.DoCreateHandle() = x.CreateHandle() 
    /// Creating the dummy form object can crash on Mono Mac, and then prints a nasty background
    /// error during finalization of the half-initialized object...
    override x.Finalize() = ()
    
/// This is the event loop implementation for winforms
let WinFormsEventLoop(lcid : int option) = 
    let mainForm = new DummyForm() 
    do mainForm.DoCreateHandle();
    // Set the default thread exception handler
    let restart = ref false
    { new Microsoft.FSharp.Compiler.Interactive.IEventLoop with
         member x.Run() =  
             restart := false
             Application.Run()
             !restart
         member x.Invoke (f: unit -> 'T) : 'T =   
            if not mainForm.InvokeRequired then 
                f() 
            else

                // Workaround: Mono's Control.Invoke returns a null result.  Hence avoid the problem by 
                // transferring the resulting state using a mutable location.
                let mainFormInvokeResultHolder = ref None

                // Actually, Mono's Control.Invoke isn't even blocking (or wasn't on 1.1.15)!  So use a signal to indicate completion.
                // Indeed, we should probably do this anyway with a timeout so we can report progress from 
                // the GUI thread.
                use doneSignal = new AutoResetEvent(false)


                // BLOCKING: This blocks the stdin-reader thread until the
                // form invocation has completed.  NOTE: does not block on Mono, or did not on 1.1.15
                mainForm.Invoke(new MethodInvoker(fun () -> 
                                           try 
                                              // When we get called back, someone may jack our culture
                                              // So we must reset our UI culture every time
                                              SetCurrentUICultureForThread lcid
                                              mainFormInvokeResultHolder := Some(f ())
                                           finally 
                                              doneSignal.Set() |> ignore)) |> ignore

                //if !progress then fprintfn outWriter "RunCodeOnWinFormsMainThread: Waiting for completion signal...."
                while not (doneSignal.WaitOne(new TimeSpan(0,0,1),true)) do 
                    () // if !progress then fprintf outWriter "." outWriter.Flush()

                //if !progress then fprintfn outWriter "RunCodeOnWinFormsMainThread: Got completion signal, res = %b" (Option.isSome !mainFormInvokeResultHolder)
                !mainFormInvokeResultHolder |> Option.get

         member x.ScheduleRestart()  =   restart := true; Application.Exit()  }


#if HAVE_ACCESS_TO_SERVER_INTERNALS
let StartServer(fsiServerName) = 
    let server =
        {new Server.Shared.FSharpInteractiveServer() with
           member this.Interrupt() = 
            //printf "FSI-SERVER: received CTRL-C request...\n"
            try 
                fsiInterruptController.Interrupt()
            with e -> 
                // Final sanity check! - catch all exns - but not expected 
                assert false
                ()    
        }

    Server.Shared.FSharpInteractiveServer.StartServer(fsiServerName,server)
#endif

//----------------------------------------------------------------------------
// GUI runCodeOnMainThread
//----------------------------------------------------------------------------

#if SILVERLIGHT
#else            
     
let internal TrySetUnhandledExceptionMode() =  
    let i = ref 0 // stop inlining 
    try 
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException) 
    with _ -> 
      decr i;()

#endif // SILVERLIGHT

// Mark the main thread as STAThread since it is a GUI thread
[<EntryPoint>]
[<STAThread()>]    
let MainMain argv = 
    ignore argv
    let argv = System.Environment.GetCommandLineArgs()

    // When VFSI is running, set the input/output encoding to UTF8.
    // Otherwise, unicode gets lost during redirection.
    // It is required only under Net4.5 or above (with unicode console feature).
    if argv |> Array.exists (fun x -> x.Contains "fsi-server") then
        Console.InputEncoding <- System.Text.Encoding.UTF8 
        Console.OutputEncoding <- System.Text.Encoding.UTF8


#if DEBUG  
    if argv |> Array.exists  (fun x -> x = "/pause" || x = "--pause") then 
        Console.WriteLine("Press any key to continue...")
        Console.ReadKey() |> ignore
#endif

    try
        let console = new Microsoft.FSharp.Compiler.Interactive.ReadLineConsole()
        let getConsoleReadLine () = 
                   let probeToSeeIfConsoleWorks =
                    //if progress then fprintfn outWriter "probing to see if console works..."
                    try
                        // Probe to see if the console looks functional on this version of .NET
                        let _ = Console.KeyAvailable 
                        let c1 = Console.ForegroundColor
                        let c2 = Console.BackgroundColor
                        let _ = Console.CursorLeft <- Console.CursorLeft
                        //if progress then fprintfn outWriter "probe succeeded, we might have a console, comparing foreground (%A) and background (%A) colors, if they are the same then we're running in emacs or VS on unix and we turn off readline by default..." c1 c2
                        c1 <> c2
                    with _ -> 
                        //if progress then fprintfn outWriter "probe failed, we have no console..."
                        false 
                   if probeToSeeIfConsoleWorks then 
                       Some (fun () -> console.ReadLine())
                   else
                       None
        
        let fsiConfig0 = FsiEvaluationSession.GetDefaultConfiguration(fsi)
        let fsiConfig = 
            { // Update the configuration to include 'StartServer' and 'OptionalConsoleReadLine'
              new FsiEvaluationSessionHostConfig with 
                member __.FormatProvider = fsiConfig0.FormatProvider
                member __.FloatingPointFormat = fsiConfig0.FloatingPointFormat
                member __.AddedPrinters = fsiConfig0.AddedPrinters
                member __.ShowDeclarationValues = fsiConfig0.ShowDeclarationValues
                member __.ShowIEnumerable = fsiConfig0.ShowIEnumerable
                member __.ShowProperties = fsiConfig0.ShowProperties
                member __.PrintSize = fsiConfig0.PrintSize  
                member __.PrintDepth = fsiConfig0.PrintDepth
                member __.PrintWidth = fsiConfig0.PrintWidth
                member __.PrintLength = fsiConfig0.PrintLength
                member __.ReportUserCommandLineArgs args = fsiConfig0.ReportUserCommandLineArgs args
                member __.EventLoopRun() = fsiConfig0.EventLoopRun()
                member __.EventLoopInvoke(f) = fsiConfig0.EventLoopInvoke(f)
                member __.EventLoopScheduleRestart() = fsiConfig0.EventLoopScheduleRestart()
                member __.UseFsiAuxLib = fsiConfig0.UseFsiAuxLib

                member __.StartServer(fsiServerName) = 
#if HAVE_ACCESS_TO_SERVER_INTERNALS
                    StartServer(fsiServerName)
#else
                    failwith "--fsi-server not implemented in this version of fsi.exe"
#endif
                // Connect the configuration through to the 'fsi' Event loop
                member __.OptionalConsoleReadLine = getConsoleReadLine() }

        let fsiSession = FsiEvaluationSession (fsiConfig, argv, Console.In, Console.Out, Console.Error)
        if fsiSession.IsGui then 
            try 
                Application.EnableVisualStyles() 
            with _ -> 
                ()

            // Route GUI application exceptions to the exception handlers
            Application.add_ThreadException(new ThreadExceptionEventHandler(fun _ args -> fsiSession.ReportUnhandledException args.Exception));

            let runningOnMono = try System.Type.GetType("Mono.Runtime") <> null with e->  false        
            if not runningOnMono then 
                try 
                    TrySetUnhandledExceptionMode() 
                with _ -> 
                    ()
            
            try fsi.EventLoop <-  WinFormsEventLoop(fsiSession.LCID)
            with e ->
                printfn "Your system doesn't seem to support WinForms correctly. You will"
                printfn "need to set fsi.EventLoop use GUI windows from F# Interactive."
                printfn "You can set different event loops for MonoMac, Gtk#, WinForms and other"
                printfn "UI toolkits. Drop the --gui argument if no event loop is required."
                    

        console.SetCompletionFunction(fun (s1,s2) -> fsiSession.GetCompletions (match s1 with | Some s -> s + "." + s2 | None -> s2))
        
        fsiSession.Run() 
    with e -> printf "Exception by fsi.exe:\n%+A\n" e

    0




