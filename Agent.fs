/// The conversation with Claude: send the question, run the tools it asks for, repeat until it answers.
module CompanyBrain.Agent

open System
open System.Collections.Generic
open System.Text.Json
open Anthropic
open Anthropic.Core
open Anthropic.Models.Beta
open Anthropic.Models.Beta.Messages

let private systemPrompt () =
    let today = DateTime.UtcNow.ToString "yyyy-MM-dd"

    $"""You are the company brain: an assistant for a sales team that works across Salesforce, HubSpot and Airtable.
You have a local, synced copy of all three CRMs plus team knowledge (notes, call write-ups).

How to work:
- Answer from the data. Look things up with the tools before answering; don't guess numbers or names.
- Prefer sql_query for anything countable. Use search_knowledge for context: objections, competitors, people, pricing rules.
- When you name a deal or account, include its amount/stage where relevant, and its id in parentheses so the user can act on it.
- Writes (log_activity, create_task, update_deal_stage) change a real CRM. Only propose them when the user asks for a change
  or clearly agrees to one. The user approves each one; if they decline, accept it and move on.
- Use remember when the user shares knowledge worth keeping.
- Be brief. Lead with the answer, then the supporting rows. Use plain text, short lists, no tables wider than 5 columns.

Today is {today} (UTC)."""

let private toolDefs () : List<BetaToolUnion> =
    let tools = List<BetaToolUnion>()

    for spec in Tools.specs do
        let props = Dictionary<string, JsonElement>()

        for (name, schema) in spec.Properties do
            props[name] <- JsonSerializer.SerializeToElement schema

        tools.Add(
            BetaToolUnion(
                BetaTool(
                    Name = spec.Name,
                    Description = spec.Description,
                    InputSchema = InputSchema(Properties = props, Required = List<string>(spec.Required))
                )
            )
        )

    tools

/// Holds one conversation. Messages are only ever appended, never edited.
type Session(env: Tools.Env, model: string) =
    let client = new AnthropicClient()
    let messages = List<BetaMessageParam>()
    let tools = toolDefs ()
    let system = systemPrompt ()

    let call () =
        client.Beta.Messages.Create(
            MessageCreateParams(
                Model = model,
                MaxTokens = 16000L,
                System =
                    MessageCreateParamsSystem(
                        List<BetaTextBlockParam>([ BetaTextBlockParam(Text = system, CacheControl = BetaCacheControlEphemeral()) ])
                    ),
                Tools = tools,
                Messages = List<BetaMessageParam>(messages),
                OutputConfig = BetaOutputConfig(Effort = Effort.Medium),
                // If a safety classifier declines, the API retries on a suitable fallback model in the same call.
                Betas = List<ApiEnum<string, AnthropicBeta>>([ ApiEnum<string, AnthropicBeta>.op_Implicit AnthropicBeta.ServerSideFallback2026_07_01 ]),
                Fallbacks = BetaFallbacksParam(Default())
            )
        )

    /// Sends one user turn and keeps going until Claude stops asking for tools. Returns the final text.
    member _.Ask(question: string, onToolCall: string -> string -> unit) =
        task {
            messages.Add(BetaMessageParam(BetaUserMessageParam(Content = BetaMessageContentParam(question))))
            let answer = Text.StringBuilder()
            let mutable finished = false
            let mutable rounds = 0

            while not finished do
                rounds <- rounds + 1
                let! response = call ()
                // Only the last reply is the answer; earlier text is narration between tool calls.
                answer.Clear() |> ignore

                // Echo every block back unchanged (thinking blocks included) — the API requires it.
                let echo = List<BetaContentBlockParam>()
                let results = List<BetaContentBlockParam>()

                for block in response.Content do
                    let mutable text: BetaTextBlock = null
                    let mutable thinking: BetaThinkingBlock = null
                    let mutable redacted: BetaRedactedThinkingBlock = null
                    let mutable toolUse: BetaToolUseBlock = null

                    if block.TryPickText(&text) then
                        echo.Add(BetaContentBlockParam(BetaTextBlockParam(Text = text.Text)))
                        answer.Append(text.Text) |> ignore
                    elif block.TryPickThinking(&thinking) then
                        echo.Add(BetaContentBlockParam(BetaThinkingBlockParam(Thinking = thinking.Thinking, Signature = thinking.Signature)))
                    elif block.TryPickRedactedThinking(&redacted) then
                        echo.Add(BetaContentBlockParam(BetaRedactedThinkingBlockParam(Data = redacted.Data)))
                    elif block.TryPickToolUse(&toolUse) then
                        echo.Add(BetaContentBlockParam(BetaToolUseBlockParam(ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input)))
                        let input = JsonSerializer.SerializeToElement toolUse.Input
                        onToolCall toolUse.Name (input.GetRawText())
                        let! output, isError = Tools.run env toolUse.Name input

                        results.Add(
                            BetaContentBlockParam(
                                BetaToolResultBlockParam(
                                    ToolUseID = toolUse.ID,
                                    Content = BetaToolResultBlockParamContent(output),
                                    IsError = isError
                                )
                            )
                        )

                messages.Add(BetaMessageParam(BetaAssistantMessageParam(Content = BetaMessageContentParam(echo))))

                let stop = response.StopReason |> Option.ofObj |> Option.map string |> Option.defaultValue ""

                if results.Count > 0 then
                    messages.Add(BetaMessageParam(BetaUserMessageParam(Content = BetaMessageContentParam(results))))

                    if rounds >= 25 then
                        answer.Append("\n(Stopped after 25 tool rounds.)") |> ignore
                        finished <- true
                elif stop.Contains "refusal" then
                    answer.Append("\n(The request was declined by Claude's safety checks.)") |> ignore
                    finished <- true
                elif stop.Contains "pause_turn" then
                    () // server paused a long turn; sending the history again resumes it
                else
                    finished <- true

            return answer.ToString().Trim()
        }
