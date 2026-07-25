namespace ProcessKit

open System
open System.Runtime.InteropServices
open System.Text

/// The encoding a **legacy console program** actually writes its output in on this host — the one
/// answer needed to read a pre-UTF-8 Windows tool without mojibake.
///
/// ProcessKit decodes captured output as UTF-8 by default, and that default is deliberately not
/// changed by anything here: it is right for every modern tool, on every platform, and guessing a
/// code page for a child that emits UTF-8 would corrupt output that reads correctly today. But a
/// Windows console program written before UTF-8 — `ping`, `netstat`, `chkdsk`, most of the built-in
/// tooling, and any application still built against the ANSI/OEM CRT — writes its non-ASCII text in
/// a code page instead, so a UTF-8 decode turns every accented or Cyrillic character into `U+FFFD`.
/// This module resolves **which** code page that is, so the fix is one call rather than a research
/// project (`GetOEMCP` vs `GetConsoleOutputCP`, `chcp`, `CodePagesEncodingProvider`).
///
/// Use it through `Command.ConsoleEncoding()`, which applies the result to text stdin and both captured streams, or
/// take the `Encoding` directly from `current ()` for a `Pipeline`/`CliClient`/single-stream case.
[<RequireQualifiedAccess>]
module ConsoleEncoding =

    // `CodePagesEncodingProvider` is what teaches `Encoding.GetEncoding` the single-byte OEM/ANSI code
    // pages — 437, 850, 866, 1251, 1252 and the rest — that .NET does not carry built in: without it a
    // perfectly ordinary console code page comes back as a `NotSupportedException` rather than an
    // encoding. Registration is process-wide and ADDITIVE (`Encoding.RegisterProvider` appends to the
    // runtime's provider list), so registering on every call would grow that list by one identical
    // entry per call; `lazy` makes it exactly-once and thread-safe under its default
    // ExecutionAndPublication mode, and every later call is a read of an already-forced value.
    //
    // This needs no NuGet package. `System.Text.Encoding.CodePages` is part of the shared framework
    // AND of the targeting pack on both TFMs this library builds for (net8.0, net10.0), so the provider
    // is in-box; adding the package back would resolve to the very same assembly while putting a
    // dependency on every consumer, and net10.0's framework package-override list already supersedes
    // it. Registration stays explicit because the runtime never registers the provider on its own.
    let private codePagesRegistration =
        lazy (Encoding.RegisterProvider CodePagesEncodingProvider.Instance)

    /// UTF-8 — what a `Command` decodes with by default, and what this module resolves to wherever
    /// there is no legacy console code page to honour. Deliberately the same instance the default
    /// uses, so "this helper changed nothing" is an identity, not an approximation.
    let private utf8: Encoding = Encoding.UTF8

    /// The encoding a legacy console child of this process most likely writes its output in.
    ///
    /// **Windows:** the output code page of this process's console (what `chcp` reports, and what a
    /// child inherits), or the system OEM code page when this process has no console at all — a GUI
    /// application, a service, a detached test host. Resolved live on every call, because `chcp` can
    /// change the console's code page while the process runs.
    ///
    /// **Everywhere else:** UTF-8, the same instance `Command` already decodes with. Unix has no
    /// second, legacy console encoding to discover, so this is a genuine no-op rather than a
    /// platform-specific guess — and no P/Invoke happens on that path.
    ///
    /// **Best effort, never a failure.** A console code page the runtime has no data for falls back to
    /// UTF-8 rather than throwing: this is a convenience over a decoding default, and a builder call
    /// that threw because of the host's console settings would be worse than the mojibake it exists to
    /// prevent. A console already switched to UTF-8 (`chcp 65001`) resolves to exactly the default.
    let current () : Encoding =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            let codePage = Native.Windows.consoleOutputCodePage ()

            if codePage = utf8.CodePage then
                // A UTF-8 console needs no provider and no separate encoding object: hand back the very
                // instance the default uses, so `ConsoleEncoding()` is observably a no-op there too.
                utf8
            else
                try
                    codePagesRegistration.Force()
                    Encoding.GetEncoding codePage
                with
                | :? ArgumentException
                | :? NotSupportedException ->
                    // The console reports a code page this runtime has no data for (an exotic or
                    // out-of-range one, or a trimmed deployment that dropped the code-page data). There
                    // is nothing better to decode with than the library's own default, and failing the
                    // call would break a command over a host setting the caller does not control.
                    utf8
        else
            utf8
