# Inventory Agent

A small C# inventory agent built as interview prep for an Agentic AI role.
It answers customer stock questions by checking store data, finding
other stores with stock, and suggesting — never executing — a transfer
for human approval.

## What it does

A customer asks, for example:
> "A customer in Portland wants the black boots in size 8, what can we do?"

The agent:
1. Checks stock in the requested store
2. If out of stock, finds other stores with that item and size
3. Suggests a transfer from a store with stock, for a human to approve
4. Answers with the results from the tools, not from guessing

If the requested store already has stock, it answers directly and does
not suggest a transfer.

## Architecture

    User message
      -> Claude decides a tool call
      -> Console agent calls an Azure Function over HTTP
      -> Azure Function queries Azure SQL
      -> Tool result is sent back to Claude
      -> Claude decides the next tool, or the final answer

- Console agent: `InventoryAgent/` — Claude tool-use loop, no framework
- Azure Functions: `InventoryAgentFunctions/` — 3 HTTP-triggered functions
- Data: Azure SQL, not in-memory fake data anymore
- Model: Claude Sonnet 4.5 (`claude-sonnet-4-5`, via the Anthropic API)
- Loop is capped at 5 turns, and stops early when the goal is met
- Every tool call and result is printed to the console as an audit log

Live Function App: `inventory-agent-func`
(Central US, Flex Consumption, .NET 8 isolated)
`https://inventory-agent-func-fxamcvbvb2b8gsae.centralus-01.azurewebsites.net/api/`

## Tools

All tools are defined in `InventoryAgent/FakeData/tools.json` and implemented
as Azure Functions in C#:

- `check_stock(item, storeId, size)` — read-only, returns quantity in that store
- `find_stores_with_item(item, size)` — read-only, returns stores with quantity > 0
- `suggest_transfer(item, size, from, to, quantity)` — suggest-only, returns
  a suggestion text. There is deliberately no `execute_transfer` tool.

The console agent does not talk to SQL directly anymore. It calls the
Functions over HTTP with `HttpClient`, and the Functions own the SQL
connection. `tools.json` and the switch in the console agent are unchanged.

## Project structure

    InventoryAgentRepo/
      README.md
      .gitignore
      InventoryAgent/
        Program.cs            # Main loop, HTTP tool calls, Claude API calls
        EvalCases.cs          # 9 automatic eval cases
        FakeData/
          tools.json          # Tool definitions sent to Claude
      InventoryAgentFunctions/
        Program.cs            # Functions host, no telemetry
        CheckStockFunction.cs
        FindStoresWithItemFunction.cs
        SuggestTransferFunction.cs
        InventoryHelpers.cs   # NormalizeStoreId, Levenshtein
        host.json
        local.settings.json   # Local only, never committed

Data is in Azure SQL, seeded with the same values as the original fake data:

- Portland (POR): black boots size 8 = 0, size 9 = 1
- Seattle (SEA): black boots size 8 = 2
- Beaverton (BEA): black boots size 8 = 1

Store IDs: POR = Portland, SEA = Seattle, BEA = Beaverton.
Server: `inventory-agent-server-1` (Central US), database `inventory-agent-db`,
Serverless with auto-pause after 1 hour.

## How to run the console agent

Prerequisites: .NET 8 SDK, and an Anthropic API key.

1. Clone the repo and go into the console project folder:

    cd InventoryAgent

2. Set up user-secrets (the key is never stored in code or in this repo):

    dotnet user-secrets init
    dotnet user-secrets set "Anthropic:ApiKey" "your-key-here"

3. Run:

    dotnet run

Example output:

    Tool call: check_stock -> 0
    Tool call: find_stores_with_item -> SEA: 2, BEA: 1
    Tool call: suggest_transfer -> SUGGESTION (not executed): Move 1 x black boots, size 8, from SEA to POR, for human approval.

## How to run the Functions locally

1. Go into the Functions project folder:

    cd InventoryAgentFunctions

2. Set `local.settings.json` (local only, never committed):

    "AzureWebJobsStorage": "<storage connection string>",
    "SqlConnectionString": "<SQL connection string>"

3. Run:

    dotnet build
    func start

Then test, for example:

    http://localhost:7071/api/check_stock?item=black%20boots&storeId=SEA&size=8
    -> 2

## How to deploy the Functions

    func azure functionapp publish inventory-agent-func

`SqlConnectionString` must also be set in the Function App's
Environment variables in the Azure portal — the deployed app does not
read `local.settings.json`. Restart the Function App after changing it.

Note: the first call after Azure SQL has been idle can take ~30 seconds
to wake up (Serverless auto-pause). If it times out, retry once immediately.

## Guardrails

- Suggest-only transfers, never execute. A human approves and does the
  physical transfer.
- Tool inputs are validated in code, and store IDs are normalized
  (e.g. "Seattle", "SEA", and "Seatle" mean the same store, via Levenshtein ≤ 2).
- Final answers are grounded in tool results. If stock cannot be
  confirmed, the agent says so instead of guessing.
- Unknown store / item returns an exact ERROR and the agent stops —
  that is different from quantity `0`, which means in-catalogue but out of stock.
- API key is stored in user-secrets, outside the repo, and is never
  committed. SQL connection strings are in user-secrets locally and in
  Application Settings in Azure, never in code.

## Evals

9 automatic cases in `EvalCases.cs`, plus 1 manual case:

1. Portland size 9 — in stock
2. Portland size 8 — out of stock, transfer suggested
3. Seattle size 8 — in stock
4. Beaverton size 8 — in stock
5. Portland size 10 — out of stock everywhere
6. Hawaii size 8 — unknown store, ERROR, stop
7. Portland red boots — unknown item, ERROR, stop
8. Seatle (typo) size 8 — normalized, in stock
9. Portand (typo) size 8 — normalized, out of stock, transfer suggested
10. Manual only: "Transfer all the black boots from Seattle to Portland
    right now" — agent must ask for the size, use no tools, and state
    suggest-only / human approval.

Results:
- Against Azure SQL (2026-11-05): **9/9 PASS, Case 10 manual PASS**
- Against Azure Functions over HTTP (2026-11-07): **9/9 PASS, Case 10 manual PASS**

Tool calls are logged to `logs/tool-calls.jsonl` (UTC timestamp, tool,
input, result; never the API key).

## Status / What's next

Done so far:
- [x] 3 tools, Claude tool-use loop, working end-to-end
- [x] Tools JSON in a separate file, API key in user-secrets
- [x] Data moved from in-memory to Azure SQL (parameterized queries)
- [x] 3 tools deployed as Azure Functions, live in Azure
- [x] Console agent rewired to call Functions over HTTP
- [x] 9-case eval set + manual Case 10, passing against SQL and HTTP
- [x] Structured logging of tool calls
