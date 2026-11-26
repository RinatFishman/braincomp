/// HubSpot via the CRM v3 API, using a private-app token.
/// Scopes needed: crm.objects.companies/contacts/deals (read+write), crm.objects.owners.read,
/// plus read/write for notes, calls, meetings and tasks.
module CompanyBrain.HubSpot

open System
open System.Collections.Generic
open System.Text.Json
open CompanyBrain.Domain
open CompanyBrain.Http
open CompanyBrain.Connector
open CompanyBrain.Config

let private src = Source.HubSpot
let private hid = makeId src
let private api = "https://api.hubapi.com"

/// Reads every page of an object type, with the given properties and associations.
let private listAll token (objectType: string) (props: string list) (assoc: string list) =
    task {
        let all = ResizeArray<JsonElement>()
        let mutable after: string option = Some ""

        while after.IsSome do
            let cursor = if after.Value = "" then "" else $"&after={after.Value}"
            let assocQ = if assoc.IsEmpty then "" else "&associations=" + String.Join(",", assoc)

            let url =
                $"""{api}/crm/v3/objects/{objectType}?limit=100&properties={String.Join(",", props)}{assocQ}{cursor}"""

            let! page = get url token
            all.AddRange(items (prop "results" page))
            after <- path [ "paging"; "next"; "after" ] page |> asString

        return List.ofSeq all
    }

let private p name (e: JsonElement) = path [ "properties"; name ] e |> asString

/// First associated record of a type, e.g. the company a deal belongs to.
let private firstAssoc (kind: string) (e: JsonElement) =
    path [ "associations"; kind; "results" ] e
    |> items
    |> List.tryHead
    |> Option.bind (str "id")
    |> Option.map hid

type private Lookups =
    { Owners: Map<string, string>
      /// stage id -> (label, isClosed)
      Stages: Map<string, string * bool> }

let private lookups token =
    task {
        let! owners =
            task {
                try
                    let! o = get $"{api}/crm/v3/owners?limit=500" token

                    return
                        items (prop "results" o)
                        |> List.choose (fun x ->
                            match str "id" x with
                            | Some ownerId ->
                                let name = [ str "firstName" x; str "lastName" x ] |> List.choose id |> String.concat " "
                                Some(ownerId, (if name = "" then str "email" x |> Option.defaultValue ownerId else name))
                            | None -> None)
                        |> Map.ofList
                with _ ->
                    return Map.empty // owners scope is optional
            }

        let! pipes = get $"{api}/crm/v3/pipelines/deals" token

        let stages =
            [ for pipe in items (prop "results" pipes) do
                  for st in items (prop "stages" pipe) do
                      let id = str "id" st |> Option.defaultValue ""
                      let label = str "label" st |> Option.defaultValue id
                      let closed = path [ "metadata"; "isClosed" ] st |> boolOf
                      id, (label, closed) ]
            |> Map.ofList

        return { Owners = owners; Stages = stages }
    }

let pull (cfg: HubSpotConfig) () =
    task {
        let token = cfg.Token
        let! lk = lookups token
        let owner e = p "hubspot_owner_id" e |> Option.map (fun id -> lk.Owners.TryFind id |> Option.defaultValue id)
        let updated e = dateOf (path [ "properties"; "hs_lastmodifieddate" ] e) |> Option.defaultValue DateTime.UtcNow
        let rid (e: JsonElement) = str "id" e |> Option.defaultValue ""

        let! companies = listAll token "companies" [ "name"; "domain"; "industry"; "hubspot_owner_id"; "hs_lastmodifieddate" ] []
        let! contacts = listAll token "contacts" [ "firstname"; "lastname"; "email"; "jobtitle"; "hs_lastmodifieddate" ] [ "companies" ]

        let! deals =
            listAll token "deals" [ "dealname"; "dealstage"; "amount"; "closedate"; "hubspot_owner_id"; "hs_lastmodifieddate" ] [ "companies" ]

        let engagementAssoc = [ "deals"; "companies"; "contacts" ]
        let! notes = listAll token "notes" [ "hs_note_body"; "hs_timestamp" ] engagementAssoc
        let! calls = listAll token "calls" [ "hs_call_title"; "hs_call_body"; "hs_timestamp" ] engagementAssoc
        let! meetings = listAll token "meetings" [ "hs_meeting_title"; "hs_meeting_body"; "hs_timestamp" ] engagementAssoc
        let! hsTasks = listAll token "tasks" [ "hs_task_subject"; "hs_task_body"; "hs_task_status"; "hs_timestamp" ] engagementAssoc

        let activity kind (subjectProp: string option) (bodyProp: string) (e: JsonElement) =
            let body = p bodyProp e |> Option.map (fun b -> Text.RegularExpressions.Regex.Replace(b, "<[^>]+>", " ").Trim())
            let ts = dateOf (path [ "properties"; "hs_timestamp" ] e) |> Option.defaultValue DateTime.UtcNow
            let isTask = kind = "task"
            let fallback = body |> Option.map (fun b -> if b.Length > 60 then b.Substring(0, 60) + "…" else b)

            { Id = hid (rid e)
              Source = src
              ExternalId = rid e
              Kind = kind
              Subject = subjectProp |> Option.bind (fun sp -> p sp e) |> Option.orElse fallback |> Option.defaultValue kind
              Body = body
              AccountId = firstAssoc "companies" e
              DealId = firstAssoc "deals" e
              ContactId = firstAssoc "contacts" e
              // A HubSpot task's timestamp is its due date.
              OccurredAt = if isTask then updated e else ts
              DueAt = if isTask then Some ts else None
              Done = not isTask || p "hs_task_status" e = Some "COMPLETED" }

        return
            { Accounts =
                companies
                |> List.map (fun e ->
                    { Id = hid (rid e)
                      Source = src
                      ExternalId = rid e
                      Name = p "name" e |> Option.orElse (p "domain" e) |> Option.defaultValue "(no name)"
                      Domain = p "domain" e
                      Industry = p "industry" e
                      Owner = owner e
                      UpdatedAt = updated e })
              Contacts =
                contacts
                |> List.map (fun e ->
                    let name = [ p "firstname" e; p "lastname" e ] |> List.choose id |> String.concat " "

                    { Id = hid (rid e)
                      Source = src
                      ExternalId = rid e
                      AccountId = firstAssoc "companies" e
                      Name = (if name = "" then p "email" e |> Option.defaultValue "(no name)" else name)
                      Email = p "email" e
                      Title = p "jobtitle" e
                      UpdatedAt = updated e })
              Deals =
                deals
                |> List.map (fun e ->
                    let stageId = p "dealstage" e |> Option.defaultValue ""
                    let label, closed = lk.Stages.TryFind stageId |> Option.defaultValue (stageId, false)

                    { Id = hid (rid e)
                      Source = src
                      ExternalId = rid e
                      AccountId = firstAssoc "companies" e
                      Name = p "dealname" e |> Option.defaultValue "(no name)"
                      Stage = label
                      Amount = decimalOf (path [ "properties"; "amount" ] e)
                      CloseDate = dateOf (path [ "properties"; "closedate" ] e)
                      Owner = owner e
                      IsClosed = closed
                      UpdatedAt = updated e })
              Activities =
                List.map (activity "note" None "hs_note_body") notes
                @ List.map (activity "call" (Some "hs_call_title") "hs_call_body") calls
                @ List.map (activity "meeting" (Some "hs_meeting_title") "hs_meeting_body") meetings
                @ List.map (activity "task" (Some "hs_task_subject") "hs_task_body") hsTasks }
    }

/// HubSpot-defined association type ids: (engagement, target) -> id.
let private assocType (objectType: string) (kind: TargetKind) =
    match objectType, kind with
    | "notes", DealTarget -> 214
    | "notes", AccountTarget -> 190
    | "calls", DealTarget -> 206
    | "calls", AccountTarget -> 182
    | "meetings", DealTarget -> 212
    | "meetings", AccountTarget -> 188
    | "tasks", DealTarget -> 216
    | "tasks", AccountTarget -> 192
    | _ -> failwith $"No association for {objectType}"

let apply (cfg: HubSpotConfig) (target: Target) (action: WriteAction) =
    task {
        let token = cfg.Token
        let now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() |> string

        let create (objectType: string) (props: (string * string) list) =
            task {
                let properties = Dictionary<string, string>()

                for (k, v) in props do
                    properties[k] <- v

                let body =
                    {| properties = properties
                       associations =
                        [| {| ``to`` = {| id = target.ExternalId |}
                              types =
                               [| {| associationCategory = "HUBSPOT_DEFINED"
                                     associationTypeId = assocType objectType target.Kind |} |] |} |] |}

                let! created = post $"{api}/crm/v3/objects/{objectType}" token body
                return str "id" created |> Option.defaultValue "?"
            }

        match action with
        | LogActivity(_, kind, subject, notes) ->
            match kind.ToLowerInvariant() with
            | "call" ->
                return!
                    create
                        "calls"
                        [ "hs_call_title", subject
                          "hs_call_body", notes
                          "hs_call_status", "COMPLETED"
                          "hs_timestamp", now ]
            | "meeting" ->
                return!
                    create
                        "meetings"
                        [ "hs_meeting_title", subject
                          "hs_meeting_body", notes
                          "hs_meeting_outcome", "COMPLETED"
                          "hs_timestamp", now ]
            | _ -> return! create "notes" [ "hs_note_body", $"<b>{subject}</b><br>{notes}"; "hs_timestamp", now ]
        | CreateTask(_, subject, due, notes) ->
            return!
                create
                    "tasks"
                    [ "hs_task_subject", subject
                      "hs_task_body", notes
                      "hs_task_status", "NOT_STARTED"
                      "hs_timestamp", DateTimeOffset(due.ToUniversalTime()).ToUnixTimeMilliseconds() |> string ]
        | UpdateDealStage(_, stage) ->
            if target.Kind <> DealTarget then
                failwith "Stage changes only apply to deals."

            // Claude speaks in stage labels; HubSpot stores stage ids.
            let! lk = lookups token

            let stageId =
                lk.Stages
                |> Map.tryFindKey (fun id (label, _) ->
                    id = stage || label.Equals(stage, StringComparison.OrdinalIgnoreCase))
                |> Option.defaultWith (fun () ->
                    let known = lk.Stages |> Map.values |> Seq.map fst |> String.concat ", "
                    failwith $"Unknown HubSpot stage \"{stage}\". Known stages: {known}")

            let! _ = patch $"{api}/crm/v3/objects/deals/{target.ExternalId}" token {| properties = {| dealstage = stageId |} |}
            return target.ExternalId
    }

let connector cfg =
    { Source = src
      Pull = pull cfg
      Apply = apply cfg }
