/// Compile-time stand-ins for the values the guides introduce in prose around a
/// sample ("`cmd` is the command you built above", "`proc` is the running child").
/// Every generated snippet unit opens this module, so a block that starts mid-story
/// still compiles - with the real ProcessKit types, so a renamed or re-typed API
/// still breaks the build.
///
/// Nothing here is ever executed: the harness is compiled, never run, so the null
/// stand-ins never reach a call. Keep them typed, keep them boring - a fixture that
/// guesses the wrong type turns into a false failure on an innocent doc change.
module DocSnippets.Fixtures

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ProcessKit

/// A value of the requested type that must never be evaluated.
let private undefined<'T> () : 'T = Unchecked.defaultof<'T>

// Commands and clients.
let cmd: Command = undefined ()
let command: Command = undefined ()
let pipeline: Pipeline = undefined ()
let git: CliClient = undefined ()
let supervisor: Supervisor = undefined ()

// Live children and containers.
let proc: RunningProcess = undefined ()
let a: RunningProcess = undefined ()
let b: RunningProcess = undefined ()
let group: ProcessGroup = undefined ()
let options: ProcessGroupOptions = undefined ()
let external: Diagnostics.Process = undefined ()

// Results the prose has already obtained.
let result: ProcessResult<string> = undefined ()
let outcome: Outcome = undefined ()

// Ambient plumbing.
let runner: IProcessRunner = undefined ()
let logger: ILogger = undefined ()
let services: IServiceCollection = undefined ()
let shutdownToken: CancellationToken = undefined ()
let appLifetimeToken: CancellationToken = undefined ()

// ---------------------------------------------------------------------------
// The reader's own code
// ---------------------------------------------------------------------------
// Names the guides use as a stand-in for whatever the reader plugs in - the JSON
// payload their tool prints, the handler they run per line, the code under test.
// The guides never define these, so the harness must, and their shapes come from
// how the samples call them. A sample that needs a NEW placeholder fails with a
// clear "not defined" error pointing at the markdown line: add it here, or mark
// the block with `docsnippet:ignore`.

/// `type Widget = { Name: string; Count: int }`, as docs/commands.md spells it out.
type Widget = { Name: string; Count: int }

let bigInput: string seq = undefined ()
let files: string list = undefined ()
let handle (_line: string) : unit = undefined ()
let healthCheck () : Task<bool> = undefined ()
let pingWorkerAsync () : Task<bool> = undefined ()
let deploy (_runner: IProcessRunner) : Task<unit> = undefined ()
let installThenRetry () : unit = undefined ()
let scheduleRetry () : unit = undefined ()
let fail (_error: ProcessError) : unit = undefined ()
