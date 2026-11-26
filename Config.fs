/// All settings come from environment variables (or a .env file next to where you run it).
module CompanyBrain.Config

open System
open System.IO

let private env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

let private envOr name fallback = env name |> Option.defaultValue fallback

/// Loads KEY=VALUE lines from ./.env without overriding variables already set.
let loadDotEnv () =
    let path = Path.Combine(Directory.GetCurrentDirectory(), ".env")

    if File.Exists path then
        for line in File.ReadAllLines path do
            let line = line.Trim()

            if line <> "" && not (line.StartsWith "#") && line.Contains "=" then
                let i = line.IndexOf '='
                let key = line.Substring(0, i).Trim()
                let value = line.Substring(i + 1).Trim().Trim('"')

                if isNull (Environment.GetEnvironmentVariable key) then
                    Environment.SetEnvironmentVariable(key, value)

type SalesforceConfig =
    { InstanceUrl: string
      AccessToken: string option
      ClientId: string option
      ClientSecret: string option
      ApiVersion: string }

type HubSpotConfig = { Token: string }

/// Airtable has no fixed schema, so table and field names are configurable.
type AirtableConfig =
    { Token: string
      BaseId: string
      AccountsTable: string
      ContactsTable: string
      DealsTable: string
      ActivitiesTable: string }

let dbPath () = envOr "BRAIN_DB" "brain.db"

let model () = envOr "BRAIN_MODEL" "claude-opus-5-5"

let salesforce () =
    env "SF_INSTANCE_URL"
    |> Option.map (fun url ->
        { InstanceUrl = url.TrimEnd '/'
          AccessToken = env "SF_ACCESS_TOKEN"
          ClientId = env "SF_CLIENT_ID"
          ClientSecret = env "SF_CLIENT_SECRET"
          ApiVersion = envOr "SF_API_VERSION" "v62.0" })

let hubspot () =
    env "HUBSPOT_TOKEN" |> Option.map (fun t -> { HubSpotConfig.Token = t })

let airtable () =
    match env "AIRTABLE_TOKEN", env "AIRTABLE_BASE_ID" with
    | Some token, Some baseId ->
        Some
            { Token = token
              BaseId = baseId
              AccountsTable = envOr "AIRTABLE_ACCOUNTS_TABLE" "Accounts"
              ContactsTable = envOr "AIRTABLE_CONTACTS_TABLE" "Contacts"
              DealsTable = envOr "AIRTABLE_DEALS_TABLE" "Deals"
              ActivitiesTable = envOr "AIRTABLE_ACTIVITIES_TABLE" "Activities" }
    | _ -> None
