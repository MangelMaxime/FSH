﻿/// The core shell loop is defined and run here.
/// It prompts the user for input, then processes the result before repeating.
/// In addition, some ancillary functions like process launching are also defined.

open System
open System.IO
open System.Diagnostics
open System.ComponentModel
open Constants
open Model
open Builtins
open LineParser
open LineReader
open Interactive
open System.Runtime.InteropServices

[<EntryPoint>]
let main _ =

    // Below is the opening intro and help info lines of FSH. 
    // They are invoked here so fsi can be instantiated, putting it in scope of code operations below.

    Console.CursorVisible <- false  // Generally, the cursor is hidden when writing text that isn't from the user. This is to prevent an ugly 'flicker'.
    apply Colours.title
    printfn " -- FSH: FSharp Shell -- "
    apply Colours.neutral
    printf "starting FSI..." // Booting FSI takes a short but noticeable amount of time.
    let fsi = Fsi ()
    printfn "done"
    printfn "For a list of commands type '?' or 'help'"
   
    /// Attempts to run an executable (not a builtin like cd or dir) and to feed the result to the output.
    let rec launchProcess fileName args writeOut writeError =
        use op = // As Process is IDisposable, 'use' here ensures it is cleaned up.
            ProcessStartInfo(fileName, args |> String.concat " ",
                UseShellExecute = false,
                RedirectStandardOutput = true, // Output is redirected so it can be captured by the events below.
                RedirectStandardError = true, // Error is also redirected for capture.
                RedirectStandardInput = false) // Note we don't redirect input, so that regular console input can be sent to the process.
            |> fun i -> new Process (StartInfo = i) // Because Process is IDisposable, we use the recommended 'new' syntax.

        op.OutputDataReceived.Add(fun e -> writeOut e.Data |> ignore) // These events capture output and error, and feed them into the writeMethods.
        op.ErrorDataReceived.Add(fun e -> writeError e.Data |> ignore)
        Console.CursorVisible <- true // so when receiving input from the child process, it has a cursor

        try
            op.Start () |> ignore

            op.BeginOutputReadLine () // Necessary so that the events above will fire: the process is asynchronously listened to.
            op.WaitForExit ()
            op.CancelOutputRead ()
        with
            | :? Win32Exception as ex -> // Even on linux/osx, this is the exception thrown.
                // If on windows and the error the file isn't an executable, try piping through explorer.
                // This will cause explorer to query the registry for the default handler program.
                if ex.Message = notExecutableError && RuntimeInformation.IsOSPlatform OSPlatform.Windows then
                    launchProcess "explorer" (fileName::args) writeOut writeError
                else
                    // Will USUALLY occur when trying to run a process that doesn't exist.
                    // But running something you don't have rights too will also throw this.
                    writeError (sprintf "%s: %s" fileName ex.Message)
        // Hide the cursor.
        Console.CursorVisible <- false
    
    /// Attempts to run either help, a builtin, or an external process based on the given command and args
    let runCommand command args writeOut writeError =
        // Help (or ?) are special builtins, not part of the main builtin map (due to loading order).
        if command = "help" || command = "?" then
            help args writeOut writeError
        else
            match Map.tryFind command builtinMap with
            | Some f -> 
                f args writeOut writeError
            | None -> // If no builtin is found, try to run the users input as a execute process command.
                launchProcess command args writeOut writeError

    /// Attempts to run code as an expression or interaction. 
    /// If the last result is not empty, it is set as a value that is applied to the code as a function parameter.
    let runCode lastResult (code: string) writeOut writeError =
        let source = 
            if code.EndsWith ')' then code.[1..code.Length-2]
            else code.[1..]

        if lastResult = "" then 
            fsi.EvalInteraction source writeOut writeError
        else
            // In the code below, the piped val is type annotated and piped into the expression
            // This reduces the need for command line code to have type annotations for string.
            let toEval = 
                if code = "(*)" then // the (*) expression in special, as it treats the piped value as code to be evaluated
                    lastResult
                elif lastResult.Contains "\r\n" then
                    lastResult.Split ([|"\r\n"|], StringSplitOptions.RemoveEmptyEntries) // Treat a multiline last result as a string array.
                    |> Array.map (fun s -> s.Replace("\"", "\\\""))
                    |> String.concat "\";\"" 
                    |> fun lastResult -> sprintf "let (piped: string[]) = [|\"%s\"|] in piped |> (%s)" lastResult source
                else // If no line breaks, the last result is piped in as a string.
                    sprintf "let (piped: string) = \"%s\" in piped |> (%s)" lastResult source
                // Without the type annotations above, you would need to write (fun (s:string) -> ...) rather than just (fun s -> ...)
            fsi.EvalExpression toEval writeOut writeError
            
    /// The implementation of the '>> filename' token. Takes the piped in content and saves it to a file.
    let runOut content path _ writeError = 
        try
            File.WriteAllText (path, content)
        with
            | ex -> 
                writeError (sprintf "Error writing to out %s: %s" path ex.Message)
    
    /// Write methods provides the outputwriter object and write out, write error methods to be passed to a token evaluator.
    /// If this is the last token, these will print to Console out. 
    /// Otherwise the outputWriter will fill with written content, to be piped to the next token.
    let writeMethods isLastToken =
        let output = OutputWriter ()
        let writeOut, writeError =
            if isLastToken then 
                (fun (s:string) ->
                    apply Colours.goodOutput
                    Console.WriteLine s),
                (fun (s:string) ->
                    apply Colours.errorOutput
                    Console.WriteLine s)
            else
                output.writeOut, output.writeError
        output, writeOut, writeError

    /// Handles running a given token, e.g. a command, pipe, code or out.
    /// Output is printed into string builders if intermediate tokens, or to the console out if the last.
    /// In this way, the last token can print in real time.
    let processToken isLastToken lastResult token =
        match lastResult with
        | Error _ -> lastResult
        | Ok s ->
            let output, writeOut, writeError = writeMethods isLastToken
            match token with
            | Command (name, args) ->
                let args = if s <> "" then args @ [s] else args
                runCommand name args writeOut writeError
            | Code code ->
                runCode s code writeOut writeError
            | Pipe -> 
                writeOut s // Pipe uses the writeOut function to set the next content to be the pipedin last result
            | Out path ->
                runOut s path writeOut writeError
            | _ -> () // The Token DU also includes presentation only tokens, like linebreaks and whitespace. These are ignored.
            output.asResult ()

    /// Splits up what has been entered into a set of tokens, then runs each in turn feeding the result of the previous as the input to the next.
    /// The last token to be processed prints directly to the console out.
    let processEntered (s : string) =
        if String.IsNullOrWhiteSpace s then () // nothing specified so just loop
        else 
            let parts = parts s
            let tokens = tokens parts
            let lastToken = List.last tokens

            (Ok "", tokens) // The fold starts with an empty string as the first 'piped' value
            ||> List.fold (fun lastResult token -> 
                processToken (token = lastToken) lastResult token)
            |> ignore // The last token prints directly to the console out, and therefore the final result is ignored.

    /// The coreloop waits for input, runs that input, and repeats. 
    /// It also handles the special exit command, quiting the loop and thus the process.
    /// This function is tail call optimised, so can loop forever until 'exit' is entered.
    let rec coreLoop prior =
        apply Colours.prompt
        printf "%s %s> " promptName (currentDir ())
        // Here is called a special function from LineReader.fs that accepts tabs and the like.
        let entered = readLine prior
        if entered.Trim() = "exit" then ()
        else
            processEntered entered
            coreLoop (entered::prior)

    // Start the core loop with no prior command history. FSH begins!
    coreLoop []

    0
