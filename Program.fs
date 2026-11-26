module CompanyBrain.Program

open System
open System.Text.Json
open CompanyBrain.Domain

let usage =
    """company brain — one memory for Salesforce, HubSpot and Airtable

  brain demo                      load made-up demo data (try this first)
  brain sync [salesforce|hubspot|airtable]
                                  pull records from the connected CRMs
  brain ask "question"            ask one question (needs ANTHROPIC_API_KEY)
  brain chat                      keep a conversation going
  brain remember "text" [--account NAME]
                                  save a piece of knowledge
  brain ingest <folder>           import .md / .txt notes from a folder
  brain stale [days] [min_amount] open deals with no activity (default 30 days)
  brain account <name>            everything about one account
  brain search <words>            search notes and call write-ups
  brain sql "<SELECT ...>"        run a read-only query
  brain status                    what's connected and what's stored
  brain audit                     every change the brain proposed, and what happened
  brain tool <name> '<json>'      run one of Claude's tools by hand (for testing)

Settings go in environment variables or a .env file — see README.md."""

let connectors () =
    [ Config.salesforce () |> Option.map Salesforce.connector
      Config.hubspot () |> Option.map HubSpot.connector
      Config.airtable () |> Option.map Airtable.connector
      Some Demo.connector ]
    |> List.choose id
    |> List.map (fun c -> c.Source, c)
    |> Map.ofList

let pretty (json: string) =
    JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, JsonSerializerOptions(WriteIndented = true, Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

let sync (db: Microsoft.Data.Sqlite.SqliteConnection) (only: Source option) =
    task {
        let targets =
            connectors ()
            |> Map.toList
            |> List.filter (fun (s, _) -> s <> Demo)
            |> List.filter (fun (s, _) -> only.IsNone || only = Some s)

        if targets.IsEmpty then
            printfn "No CRM is connected yet. Add credentials to .env (see README.md), or run `brain demo`."

        for (source, c) in targets do
            printf $"Syncing {source.Key}… "

            try
                let! snap = c.Pull()
                Store.saveSnapshot db source snap

                printfn
                    $"{snap.Accounts.Length} accounts, {snap.Contacts.Length} contacts, {snap.Deals.Length} deals, {snap.Activities.Length} activities"
            with ex ->
                let msg =
                    match ex with
                    | Http.CrmHttpError(status, body) -> $"HTTP {status}: {body}"
                    | _ -> ex.Message

                printfn $"failed: {msg}"
    }

let session db =
    let ro = Store.openReadOnly (Config.dbPath ())

    Agent.Session(
        { Db = db
          ReadOnly = ro
          Connectors = connectors ()
          Approve = Actions.askOnConsole },
        Config.model ()
    )

let showToolCall (name: string) (input: string) =
    Console.ForegroundColor <- ConsoleColor.DarkGray
    let shown = if input.Length > 160 then input.Substring(0, 160) + "…" else input
    printfn $"  · {name} {shown}"
    Console.ResetColor()

let requireKey () =
    if String.IsNullOrEmpty(Environment.GetEnvironmentVariable "ANTHROPIC_API_KEY") then
        eprintfn "Set ANTHROPIC_API_KEY (in your shell or .env) to talk to the brain."
        exit 2

[<EntryPoint>]
let main argv =
    Config.loadDotEnv ()
    use db = Store.openDb (Config.dbPath ())
    let args = List.ofArray argv

    let run =
        task {
            match args with
            | [ "demo" ] ->
                let snap = Demo.snapshot ()
                Store.saveSnapshot db Demo snap

                for note in Demo.notes () do
                    Store.saveNote db note

                printfn $"Loaded demo data: {snap.Accounts.Length} accounts, {snap.Deals.Length} deals, {Demo.notes().Length} notes."
                printfn "Try:  brain stale 30     or     brain ask \"Which big deals have gone quiet, and why?\""
                return 0
            | [ "sync" ] ->
                do! sync db None
                return 0
            | [ "sync"; s ] ->
                match Source.Parse s with
                | Some src ->
                    do! sync db (Some src)
                    return 0
                | None ->
                    eprintfn $"Unknown CRM \"{s}\"."
                    return 1
            | "ask" :: rest when not rest.IsEmpty ->
                requireKey ()
                let! answer = (session db).Ask(String.Join(" ", rest), showToolCall)
                printfn $"\n{answer}"
                return 0
            | [ "chat" ] ->
                requireKey ()
                let s = session db
                printfn "Ask anything about your accounts and deals. Empty line to quit."
                let mutable go = true

                while go do
                    printf "\n> "

                    match Console.ReadLine() with
                    | null
                    | "" -> go <- false
                    | q ->
                        let! answer = s.Ask(q, showToolCall)
                        printfn $"\n{answer}"

                return 0
            | "remember" :: rest when not rest.IsEmpty ->
                let text, account =
                    match List.tryFindIndex ((=) "--account") rest with
                    | Some i when i + 1 < rest.Length -> String.Join(" ", List.take i rest), Some rest[i + 1]
                    | _ -> String.Join(" ", rest), None

                let note = Knowledge.remember db text account
                let linked = note.AccountId |> Option.map (fun a -> $" (linked to {a})") |> Option.defaultValue ""
                printfn $"Saved{linked}."
                return 0
            | [ "ingest"; folder ] ->
                let notes = Knowledge.ingestFolder db folder
                let linked = notes |> List.filter (fun n -> n.AccountId.IsSome) |> List.length
                printfn $"Imported {notes.Length} notes ({linked} linked to an account)."
                return 0
            | "stale" :: rest ->
                let days = rest |> List.tryItem 0 |> Option.map int |> Option.defaultValue 30
                let minAmount = rest |> List.tryItem 1 |> Option.map float |> Option.defaultValue 0.0
                printfn "%s" (pretty (Store.staleDeals db days minAmount))
                return 0
            | "account" :: rest when not rest.IsEmpty ->
                match Store.accountOverview db (String.Join(" ", rest)) with
                | Some json -> printfn "%s" (pretty json)
                | None -> printfn "No matching account."

                return 0
            | "search" :: rest when not rest.IsEmpty ->
                printfn "%s" (pretty (Store.searchKnowledge db (String.Join(" ", rest)) 8))
                return 0
            | [ "sql"; q ] ->
                use ro = Store.openReadOnly (Config.dbPath ())

                match Store.runReadOnlySql ro q with
                | Ok json -> printfn "%s" (pretty json)
                | Error e -> eprintfn "%s" e

                return 0
            | [ "status" ] ->
                let configured =
                    connectors () |> Map.keys |> Seq.filter ((<>) Demo) |> Seq.map (fun s -> s.Key) |> List.ofSeq

                printfn $"""database:   {IO.Path.GetFullPath(Config.dbPath ())}"""
                printfn $"""connected:  {(if configured.IsEmpty then "none (demo only)" else String.Join(", ", configured))}"""
                printfn $"""model:      {Config.model ()}"""

                for (k, v) in Store.stats db do
                    printfn $"""{k + ":",-11} {(if v = "" then "-" else v)}"""

                return 0
            | [ "tool"; name; json ] ->
                // Runs one of Claude's tools directly — handy for testing without an API key.
                use ro = Store.openReadOnly (Config.dbPath ())

                let env: Tools.Env =
                    { Db = db
                      ReadOnly = ro
                      Connectors = connectors ()
                      Approve = Actions.askOnConsole }

                let! output, isError = Tools.run env name (JsonDocument.Parse(json).RootElement)
                printfn "%s%s" (if isError then "ERROR: " else "") output
                return (if isError then 1 else 0)
            | [ "audit" ] ->
                let _, rows, _ = Store.query db "SELECT at, source, approved, action, result FROM audit_log ORDER BY id DESC LIMIT 50" [] 50

                if rows.IsEmpty then printfn "No changes proposed yet."

                for r in rows do
                    let mark = if string r["approved"] = "1" then "✓" else "✗"
                    printfn $"""{r["at"]} {mark} [{r["source"]}] {r["action"]}  →  {r["result"]}"""

                return 0
            | _ ->
                printfn "%s" usage
                return (if args.IsEmpty || args = [ "help" ] then 0 else 1)
        }

    run.GetAwaiter().GetResult()
