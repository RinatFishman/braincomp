/// The tools Claude can use. Reads run freely; writes go through Actions (and your approval).
module CompanyBrain.Tools

open System
open System.Text.Json
open Microsoft.Data.Sqlite
open CompanyBrain.Domain
open CompanyBrain.Connector

type ToolSpec =
    { Name: string
      Description: string
      /// JSON Schema "properties" as (name, schema) pairs.
      Properties: (string * obj) list
      Required: string list }

let private s (desc: string) = box {| ``type`` = "string"; description = desc |}
let private n (desc: string) = box {| ``type`` = "number"; description = desc |}

let specs =
    [ { Name = "sql_query"
        Description =
          "Run one read-only SQLite SELECT against the local copy of all CRMs. Use this for counts, totals, "
          + "filters and joins. Returns up to 200 rows as JSON.\n"
          + Store.schemaForModel
        Properties = [ "sql", s "A single SELECT or WITH ... SELECT statement." ]
        Required = [ "sql" ] }
      { Name = "search_knowledge"
        Description =
          "Full-text search over notes and call/meeting write-ups: things like objections, competitors, "
          + "who has influence, pricing rules. Use it whenever the 'why' or context behind a deal matters."
        Properties = [ "query", s "Keywords to search for."; "limit", n "Max results (default 8)." ]
        Required = [ "query" ] }
      { Name = "account_overview"
        Description = "Everything about one account: details, deals, contacts, recent activity, open tasks and notes."
        Properties = [ "account", s "Account name (partial is fine) or id." ]
        Required = [ "account" ] }
      { Name = "stale_deals"
        Description = "Open deals with no logged activity in at least N days, biggest first."
        Properties = [ "days", n "Quiet for at least this many days."; "min_amount", n "Only deals at least this big (default 0)." ]
        Required = [ "days" ] }
      { Name = "remember"
        Description =
          "Save a piece of knowledge to the brain (local only, not the CRM): a decision, a customer preference, "
          + "a lesson learned. Use it when the user tells you something worth keeping."
        Properties =
          [ "title", s "Short title."
            "body", s "The knowledge itself, written so a colleague understands it later."
            "account_id", s "Optional account id it belongs to."
            "deal_id", s "Optional deal id it belongs to." ]
        Required = [ "title"; "body" ] }
      { Name = "log_activity"
        Description = "WRITE: log a completed call, email, meeting or note on a deal or account in its CRM. The user is asked to approve."
        Properties =
          [ "target_id", s "Deal or account id, e.g. 'salesforce:006...'."
            "kind", s "One of: call, email, meeting, note."
            "subject", s "One-line subject."
            "notes", s "What happened, outcomes, next steps." ]
        Required = [ "target_id"; "kind"; "subject"; "notes" ] }
      { Name = "create_task"
        Description = "WRITE: create a follow-up task on a deal or account in its CRM. The user is asked to approve."
        Properties =
          [ "target_id", s "Deal or account id."
            "subject", s "What needs doing."
            "due_date", s "Due date, YYYY-MM-DD."
            "notes", s "Context for whoever does it." ]
        Required = [ "target_id"; "subject"; "due_date"; "notes" ] }
      { Name = "update_deal_stage"
        Description = "WRITE: move a deal to another stage, using a stage name that CRM already uses. The user is asked to approve."
        Properties = [ "deal_id", s "Deal id."; "stage", s "New stage name exactly as the CRM uses it." ]
        Required = [ "deal_id"; "stage" ] } ]

type Env =
    { Db: SqliteConnection
      ReadOnly: SqliteConnection
      Connectors: Map<Source, Connector>
      Approve: string -> bool }

let private getStr (input: JsonElement) name =
    match input.TryGetProperty(name: string) with
    | true, v when v.ValueKind = JsonValueKind.String && v.GetString() <> "" -> Some(v.GetString())
    | _ -> None

let private getNum (input: JsonElement) name fallback =
    match input.TryGetProperty(name: string) with
    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
    | true, v when v.ValueKind = JsonValueKind.String ->
        match Double.TryParse(v.GetString(), Globalization.CultureInfo.InvariantCulture) with
        | true, d -> d
        | _ -> fallback
    | _ -> fallback

/// Runs a tool. Returns (result text, isError).
let run (env: Env) (name: string) (input: JsonElement) =
    task {
        let need field =
            getStr input field |> Option.defaultWith (fun () -> failwith $"Missing required field \"{field}\".")

        let write action =
            task {
                let! r = Actions.execute env.Db env.Connectors env.Approve action

                return
                    match r with
                    | Ok msg -> msg, false
                    | Error msg -> msg, true
            }

        try
            match name with
            | "sql_query" ->
                return
                    match Store.runReadOnlySql env.ReadOnly (need "sql") with
                    | Ok json -> json, false
                    | Error e -> e, true
            | "search_knowledge" -> return Store.searchKnowledge env.Db (need "query") (int (getNum input "limit" 8.0)), false
            | "account_overview" ->
                return
                    match Store.accountOverview env.Db (need "account") with
                    | Some json -> json, false
                    | None -> "No matching account.", true
            | "stale_deals" -> return Store.staleDeals env.Db (int (getNum input "days" 30.0)) (getNum input "min_amount" 0.0), false
            | "remember" ->
                let note =
                    { Id = $"note:{Guid.NewGuid():N}"
                      Title = need "title"
                      Body = need "body"
                      AccountId = getStr input "account_id"
                      DealId = getStr input "deal_id"
                      Author = Some "company-brain"
                      SourcePath = None
                      CreatedAt = DateTime.UtcNow }

                Store.saveNote env.Db note
                return $"Saved note {note.Id}.", false
            | "log_activity" ->
                return! write (LogActivity(need "target_id", need "kind", need "subject", need "notes"))
            | "create_task" ->
                let due =
                    match DateTime.TryParse(need "due_date", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None) with
                    | true, d -> d
                    | _ -> failwith "due_date must be YYYY-MM-DD."

                return! write (CreateTask(need "target_id", need "subject", due, need "notes"))
            | "update_deal_stage" -> return! write (UpdateDealStage(need "deal_id", need "stage"))
            | other -> return $"Unknown tool {other}.", true
        with ex ->
            return ex.Message, true
    }
