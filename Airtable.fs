/// Airtable as a CRM. Bases differ, so fields are matched by common names
/// (case-insensitive, e.g. "Name" / "Company" / "Account Name"). Table names are set in config.
module CompanyBrain.Airtable

open System
open System.Collections.Generic
open System.Text.Json
open CompanyBrain.Domain
open CompanyBrain.Http
open CompanyBrain.Connector
open CompanyBrain.Config

let private src = Source.Airtable
let private aid = makeId src

let private tableUrl (cfg: AirtableConfig) (table: string) =
    $"https://api.airtable.com/v0/{cfg.BaseId}/{Uri.EscapeDataString table}"

let private listAll (cfg: AirtableConfig) (table: string) =
    task {
        let all = ResizeArray<JsonElement>()
        let mutable offset: string option = Some ""

        while offset.IsSome do
            let q = if offset.Value = "" then "?pageSize=100" else $"?pageSize=100&offset={offset.Value}"
            let! page = get (tableUrl cfg table + q) cfg.Token
            all.AddRange(items (prop "records" page))
            offset <- str "offset" page

        return List.ofSeq all
    }

/// A missing optional table (say, no Activities table) shouldn't stop the sync.
let private listOptional cfg table =
    task {
        try
            return! listAll cfg table
        with CrmHttpError(status, _) when status = 404 || status = 403 ->
            eprintfn $"  (Airtable table \"{table}\" not found — skipping)"
            return []
    }

/// Finds the first field whose name matches one of the candidates.
let private field (candidates: string list) (record: JsonElement) =
    match prop "fields" record with
    | None -> None
    | Some fields ->
        let byName =
            [ for f in fields.EnumerateObject() -> f.Name.ToLowerInvariant(), f.Value ]

        candidates
        |> List.tryPick (fun c -> byName |> List.tryFind (fun (n, _) -> n = c.ToLowerInvariant()) |> Option.map snd)

/// Text, or the name of a collaborator / the first item of a lookup.
let private text candidates record =
    match field candidates record with
    | Some v when v.ValueKind = JsonValueKind.Object -> str "name" v |> Option.orElse (str "email" v)
    | Some v when v.ValueKind = JsonValueKind.Array -> items (Some v) |> List.tryHead |> asString
    | v -> asString v

/// First linked record id, e.g. the account a deal is linked to.
let private link candidates record =
    field candidates record |> items |> List.tryHead |> asString |> Option.map aid

let private rid (r: JsonElement) = str "id" r |> Option.defaultValue ""
let private created (r: JsonElement) = dateOf (prop "createdTime" r) |> Option.defaultValue DateTime.UtcNow

let private accountLink = [ "Account"; "Accounts"; "Company"; "Companies" ]
let private dealLink = [ "Deal"; "Deals"; "Opportunity" ]

let pull (cfg: AirtableConfig) () =
    task {
        let! accounts = listAll cfg cfg.AccountsTable
        let! contacts = listOptional cfg cfg.ContactsTable
        let! deals = listOptional cfg cfg.DealsTable
        let! activities = listOptional cfg cfg.ActivitiesTable

        let updated r =
            text [ "Last Modified"; "Updated"; "Last Modified Time" ] r
            |> Option.bind (fun s -> dateOf (Some(JsonSerializer.SerializeToElement s)))
            |> Option.defaultValue (created r)

        return
            { Accounts =
                accounts
                |> List.map (fun r ->
                    { Id = aid (rid r)
                      Source = src
                      ExternalId = rid r
                      Name = text [ "Name"; "Company"; "Account Name"; "Company Name" ] r |> Option.defaultValue "(no name)"
                      Domain = text [ "Domain"; "Website"; "URL" ] r
                      Industry = text [ "Industry"; "Sector" ] r
                      Owner = text [ "Owner"; "Account Owner" ] r
                      UpdatedAt = updated r })
              Contacts =
                contacts
                |> List.map (fun r ->
                    { Id = aid (rid r)
                      Source = src
                      ExternalId = rid r
                      AccountId = link accountLink r
                      Name = text [ "Name"; "Full Name"; "Contact" ] r |> Option.defaultValue "(no name)"
                      Email = text [ "Email"; "E-mail" ] r
                      Title = text [ "Title"; "Job Title"; "Role" ] r
                      UpdatedAt = updated r })
              Deals =
                deals
                |> List.map (fun r ->
                    let stage = text [ "Stage"; "Status"; "Deal Stage" ] r |> Option.defaultValue "Unknown"

                    let closed =
                        match field [ "Closed"; "Is Closed" ] r with
                        | Some v -> boolOf (Some v)
                        | None -> stage.StartsWith("closed", StringComparison.OrdinalIgnoreCase)

                    { Id = aid (rid r)
                      Source = src
                      ExternalId = rid r
                      AccountId = link accountLink r
                      Name = text [ "Name"; "Deal"; "Deal Name"; "Opportunity" ] r |> Option.defaultValue "(no name)"
                      Stage = stage
                      Amount = decimalOf (field [ "Amount"; "Value"; "Deal Value"; "ARR" ] r)
                      CloseDate = dateOf (field [ "Close Date"; "Expected Close"; "Close" ] r)
                      Owner = text [ "Owner"; "Deal Owner" ] r
                      IsClosed = closed
                      UpdatedAt = updated r })
              Activities =
                activities
                |> List.map (fun r ->
                    let kind = text [ "Type"; "Kind"; "Activity Type" ] r |> Option.defaultValue "note"
                    let due = dateOf (field [ "Due Date"; "Due" ] r)

                    { Id = aid (rid r)
                      Source = src
                      ExternalId = rid r
                      Kind = kind.ToLowerInvariant()
                      Subject = text [ "Subject"; "Name"; "Title" ] r |> Option.defaultValue "(no subject)"
                      Body = text [ "Notes"; "Body"; "Description"; "Summary" ] r
                      AccountId = link accountLink r
                      DealId = link dealLink r
                      ContactId = link [ "Contact"; "Contacts" ] r
                      OccurredAt = dateOf (field [ "Date"; "When" ] r) |> Option.defaultValue (created r)
                      DueAt = due
                      Done =
                        match field [ "Done"; "Completed" ] r with
                        | Some v -> boolOf (Some v)
                        | None -> due.IsNone })
              }
    }

/// Writes go to the Activities table. Field names follow this module's defaults
/// (Subject, Type, Notes, Date, Due Date, Done, Deal, Account); typecast adds missing select options.
let apply (cfg: AirtableConfig) (target: Target) (action: WriteAction) =
    task {
        let createActivity (fields: (string * obj) list) =
            task {
                let f = Dictionary<string, obj>()

                for (k, v) in fields do
                    f[k] <- v

                match target.Kind with
                | DealTarget ->
                    f["Deal"] <- [| target.ExternalId |]
                    target.AccountExternalId |> Option.iter (fun a -> f["Account"] <- [| a |])
                | AccountTarget -> f["Account"] <- [| target.ExternalId |]

                let! created =
                    post
                        (tableUrl cfg cfg.ActivitiesTable)
                        cfg.Token
                        {| records = [| {| fields = f |} |]
                           typecast = true |}

                return items (prop "records" created) |> List.tryHead |> Option.bind (str "id") |> Option.defaultValue "?"
            }

        let today = DateTime.UtcNow.ToString "yyyy-MM-dd"

        match action with
        | LogActivity(_, kind, subject, notes) ->
            return!
                createActivity
                    [ "Subject", box subject
                      "Type", box kind
                      "Notes", box notes
                      "Date", box today
                      "Done", box true ]
        | CreateTask(_, subject, due, notes) ->
            return!
                createActivity
                    [ "Subject", box subject
                      "Type", box "task"
                      "Notes", box notes
                      "Date", box today
                      "Due Date", box (due.ToString "yyyy-MM-dd")
                      "Done", box false ]
        | UpdateDealStage(_, stage) ->
            if target.Kind <> DealTarget then
                failwith "Stage changes only apply to deals."

            let! _ =
                patch
                    (tableUrl cfg cfg.DealsTable + "/" + target.ExternalId)
                    cfg.Token
                    {| fields = dict [ "Stage", box stage ]
                       typecast = true |}

            return target.ExternalId
    }

let connector cfg =
    { Source = src
      Pull = pull cfg
      Apply = apply cfg }
