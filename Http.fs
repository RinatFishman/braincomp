/// Small HTTP + JSON helpers shared by the CRM connectors.
module CompanyBrain.Http

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading.Tasks

let client =
    let c = new HttpClient()
    c.Timeout <- TimeSpan.FromSeconds 60.0
    c

exception CrmHttpError of status: int * body: string

let private send (req: HttpRequestMessage) : Task<JsonElement> =
    task {
        let! payload =
            if isNull req.Content then Task.FromResult(null) else req.Content.ReadAsStringAsync()

        let mutable attempt = 0
        let mutable result: JsonElement option = None

        while result.IsNone do
            // A request can only be sent once, so build a fresh copy each attempt.
            use r = new HttpRequestMessage(req.Method, req.RequestUri)

            for h in req.Headers do
                r.Headers.TryAddWithoutValidation(h.Key, h.Value) |> ignore

            if not (isNull payload) then
                r.Content <- new StringContent(payload, Encoding.UTF8, "application/json")

            let! resp = client.SendAsync r
            let! text = resp.Content.ReadAsStringAsync()

            // Every CRM rate-limits; back off and retry a few times.
            if int resp.StatusCode = 429 && attempt < 4 then
                do! Task.Delay(TimeSpan.FromSeconds(float (pown 2 attempt)))
                attempt <- attempt + 1
            elif not resp.IsSuccessStatusCode then
                raise (CrmHttpError(int resp.StatusCode, text))
            elif String.IsNullOrWhiteSpace text then
                result <- Some(JsonDocument.Parse("{}").RootElement.Clone())
            else
                result <- Some(JsonDocument.Parse(text).RootElement.Clone())

        return result.Value
    }

let request (meth: HttpMethod) (url: string) (bearer: string) (body: obj option) =
    let req = new HttpRequestMessage(meth, url)
    req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearer)
    req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

    match body with
    | Some b -> req.Content <- new StringContent(JsonSerializer.Serialize b, Encoding.UTF8, "application/json")
    | None -> ()

    send req

let get url bearer = request HttpMethod.Get url bearer None
let post url bearer body = request HttpMethod.Post url bearer (Some body)
let patch url bearer body = request HttpMethod.Patch url bearer (Some body)

// ---------- JsonElement helpers ----------

let prop (name: string) (e: JsonElement) =
    if e.ValueKind <> JsonValueKind.Object then
        None
    else
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
        | _ -> None

/// Follows a path like ["Owner"; "Name"].
let path (names: string list) (e: JsonElement) =
    names |> List.fold (fun acc n -> acc |> Option.bind (prop n)) (Some e)

let asString (v: JsonElement option) =
    v
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.String -> Some(v.GetString())
        | JsonValueKind.Number
        | JsonValueKind.True
        | JsonValueKind.False -> Some(v.GetRawText())
        | _ -> None)
    |> Option.filter (String.IsNullOrWhiteSpace >> not)

let str name e = prop name e |> asString

let decimalOf (v: JsonElement option) =
    v
    |> Option.bind (fun v ->
        match v.ValueKind with
        | JsonValueKind.Number -> Some(v.GetDecimal())
        | JsonValueKind.String ->
            match Decimal.TryParse(v.GetString(), Globalization.NumberStyles.Any, Globalization.CultureInfo.InvariantCulture) with
            | true, d -> Some d
            | _ -> None
        | _ -> None)

let dateOf (v: JsonElement option) =
    v
    |> asString
    |> Option.bind (fun s ->
        // HubSpot sometimes sends epoch milliseconds as text.
        match Int64.TryParse s with
        | true, ms when ms > 100000000000L -> Some(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime)
        | _ ->
            match DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal ||| Globalization.DateTimeStyles.AssumeUniversal) with
            | true, d -> Some d
            | _ -> None)

let boolOf (v: JsonElement option) =
    match v with
    | Some v when v.ValueKind = JsonValueKind.True -> true
    | Some v when v.ValueKind = JsonValueKind.String -> v.GetString().Equals("true", StringComparison.OrdinalIgnoreCase)
    | _ -> false

let items (v: JsonElement option) =
    match v with
    | Some v when v.ValueKind = JsonValueKind.Array -> [ for x in v.EnumerateArray() -> x ]
    | _ -> []
