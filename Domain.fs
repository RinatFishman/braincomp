/// One shared shape for records from every CRM, so the brain can reason across all of them.
module CompanyBrain.Domain

open System

type Source =
    | Salesforce
    | HubSpot
    | Airtable
    | Demo

    member this.Key =
        match this with
        | Salesforce -> "salesforce"
        | HubSpot -> "hubspot"
        | Airtable -> "airtable"
        | Demo -> "demo"

    static member Parse(s: string) =
        match s.Trim().ToLowerInvariant() with
        | "salesforce" | "sf" -> Some Salesforce
        | "hubspot" | "hs" -> Some HubSpot
        | "airtable" | "at" -> Some Airtable
        | "demo" -> Some Demo
        | _ -> None

/// Every record keeps its id in the CRM it came from. The local id is "<source>:<externalId>".
type Account =
    { Id: string
      Source: Source
      ExternalId: string
      Name: string
      Domain: string option
      Industry: string option
      Owner: string option
      UpdatedAt: DateTime }

type Contact =
    { Id: string
      Source: Source
      ExternalId: string
      AccountId: string option
      Name: string
      Email: string option
      Title: string option
      UpdatedAt: DateTime }

type Deal =
    { Id: string
      Source: Source
      ExternalId: string
      AccountId: string option
      Name: string
      Stage: string
      Amount: decimal option
      CloseDate: DateTime option
      Owner: string option
      IsClosed: bool
      UpdatedAt: DateTime }

/// Calls, emails, meetings, tasks — anything that happened (or should happen) on a record.
type Activity =
    { Id: string
      Source: Source
      ExternalId: string
      Kind: string
      Subject: string
      Body: string option
      AccountId: string option
      DealId: string option
      ContactId: string option
      OccurredAt: DateTime
      DueAt: DateTime option
      Done: bool }

/// Knowledge the CRM doesn't hold: meeting notes, docs, things people know.
type Note =
    { Id: string
      Title: string
      Body: string
      AccountId: string option
      DealId: string option
      Author: string option
      SourcePath: string option
      CreatedAt: DateTime }

type Snapshot =
    { Accounts: Account list
      Contacts: Contact list
      Deals: Deal list
      Activities: Activity list }

    static member Empty =
        { Accounts = []
          Contacts = []
          Deals = []
          Activities = [] }

/// Changes the brain can make in a CRM. Each one is shown to you and needs a "yes" first.
type WriteAction =
    | LogActivity of dealOrAccountId: string * kind: string * subject: string * body: string
    | CreateTask of dealOrAccountId: string * subject: string * due: DateTime * notes: string
    | UpdateDealStage of dealId: string * newStage: string

    member this.TargetId =
        match this with
        | LogActivity(id, _, _, _)
        | CreateTask(id, _, _, _)
        | UpdateDealStage(id, _) -> id

    member this.Describe() =
        match this with
        | LogActivity(id, kind, subject, body) -> $"Log {kind} on {id}: \"{subject}\"\n    {body}"
        | CreateTask(id, subject, due, notes) ->
            $"""Create task on {id}: "{subject}" due {due.ToString("yyyy-MM-dd")}""" + $"\n    {notes}"
        | UpdateDealStage(id, stage) -> $"Move deal {id} to stage \"{stage}\""

let makeId (source: Source) (externalId: string) = $"{source.Key}:{externalId}"

/// Splits "<source>:<externalId>" back into its parts.
let splitId (id: string) =
    match id.IndexOf ':' with
    | -1 -> None
    | i ->
        Source.Parse(id.Substring(0, i))
        |> Option.map (fun s -> s, id.Substring(i + 1))
