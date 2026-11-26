/// Turns a proposed change into a real one: find the owning CRM, ask you, apply, record.
module CompanyBrain.Actions

open System
open Microsoft.Data.Sqlite
open CompanyBrain.Domain
open CompanyBrain.Connector

/// Asks before every write. Returns true only on an explicit "y"/"yes".
let askOnConsole (summary: string) =
    Console.ForegroundColor <- ConsoleColor.Yellow
    printfn "\n┌─ Proposed change ───────────────────────────────"
    for line in summary.Split '\n' do
        printfn $"│ {line}"
    printf "└─ Apply this? [y/N] "
    Console.ResetColor()

    match Console.ReadLine() with
    | null -> false
    | answer -> answer.Trim().ToLowerInvariant() = "y" || answer.Trim().ToLowerInvariant() = "yes"

let private resolve (conn: SqliteConnection) (id: string) =
    match Store.lookupTarget conn id with
    | None -> Error $"No deal or account with id \"{id}\". Look it up first (ids look like 'hubspot:123')."
    | Some(kind, row) ->
        let source = Source.Parse(string row["source"]) |> Option.get

        let accountExternal =
            match row.TryFind "account_id" with
            | Some(:? string as a) -> splitId a |> Option.map snd
            | _ -> None

        Ok(
            source,
            string row["name"],
            { LocalId = id
              Kind = (if kind = "deal" then DealTarget else AccountTarget)
              ExternalId = string row["external_id"]
              AccountExternalId = accountExternal }
        )

/// Runs one write end to end. The result text goes back to Claude as the tool result.
let execute
    (conn: SqliteConnection)
    (connectors: Map<Source, Connector>)
    (approve: string -> bool)
    (action: WriteAction)
    =
    task {
        match resolve conn action.TargetId with
        | Error e -> return Error e
        | Ok(source, name, target) ->
            match connectors.TryFind source with
            | None -> return Error $"{source.Key} isn't connected, so this change can't be made. Check the .env settings."
            | Some connector ->
                let summary = $"{action.Describe()}\n  record: {name}\n  in: {source.Key}"

                if not (approve summary) then
                    Store.audit conn source.Key (action.Describe()) false "declined"
                    return Error "The user declined this change. Don't retry it unless they ask."
                else
                    try
                        let! externalId = connector.Apply target action
                        let now = DateTime.UtcNow

                        // Mirror locally so follow-up questions see the change before the next sync.
                        let local kind subject body due done' =
                            Store.recordLocalActivity
                                conn
                                { Id = makeId source externalId
                                  Source = source
                                  ExternalId = externalId
                                  Kind = kind
                                  Subject = subject
                                  Body = Some body
                                  AccountId = (if target.Kind = AccountTarget then Some target.LocalId else None)
                                  DealId = (if target.Kind = DealTarget then Some target.LocalId else None)
                                  ContactId = None
                                  OccurredAt = now
                                  DueAt = due
                                  Done = done' }

                        match action with
                        | LogActivity(_, kind, subject, body) -> local (kind.ToLowerInvariant()) subject body None true
                        | CreateTask(_, subject, due, notes) -> local "task" subject notes (Some due) false
                        | UpdateDealStage(dealId, stage) -> Store.setDealStage conn dealId stage

                        Store.audit conn source.Key (action.Describe()) true $"ok: {externalId}"
                        return Ok $"Done in {source.Key} (id {externalId})."
                    with ex ->
                        let msg =
                            match ex with
                            | Http.CrmHttpError(status, body) -> $"HTTP {status}: {body}"
                            | _ -> ex.Message

                        Store.audit conn source.Key (action.Describe()) true $"failed: {msg}"
                        return Error $"{source.Key} rejected the change: {msg}"
    }
