# Inventory Agent

A small C# inventory agent built as interview prep for an Agentic AI role.
It answers customer stock questions by checking fake store data, finding
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

## Tools

All tools are defined in `FakeData/tools.json` and implemented in C#:

- `check_stock(item, storeId, size)` — read-only, returns quantity in that store
- `find_stores_with_item(item, size)` — read-only, returns stores with quantity > 0
- `suggest_transfer(item, size, from, to, quantity)` — suggest-only, returns
  a suggestion text. There is deliberately no `execute_transfer` tool.

## How it works

Direct Claude tool use, no framework:

    User message
      -> Claude decides a tool call
      -> C# code validates and runs the tool
      -> Tool result is sent back to Claude
      -> Claude decides the next tool, or the final answer

- Model: Claude Sonnet (via the Anthropic API)
- Loop is capped at 5 turns, and stops early when the goal is met
- Every tool call and result is printed to the console as an audit log

## Project structure

    InventoryAgent/
      Program.cs            # Main loop, tool methods, Claude API calls
      FakeData/
        tools.json          # Tool definitions sent to Claude
      README.md

Fake data is used on purpose, so behaviour is deterministic and testable:

- Portland (POR): black boots size 8 = 0, size 9 = 1
- Seattle (SEA): black boots size 8 = 2
- Beaverton (BEA): black boots size 8 = 1

Store IDs: POR = Portland, SEA = Seattle, BEA = Beaverton.

## How to run

Prerequisites: .NET SDK, and an Anthropic API key.

1. Clone the repo and go into the project folder:

    cd InventoryAgent

2. Set up user-secrets (the key is never stored in code or in this repo):

    dotnet user-secrets init
    dotnet user-secrets set "Anthropic:ApiKey" "your-key-here"

3. Add the required packages:

    dotnet add package Microsoft.Extensions.Configuration
    dotnet add package Microsoft.Extensions.Configuration.UserSecrets

4. Run:

    dotnet run

Example output:

    Tool call: check_stock -> 0
    Tool call: find_stores_with_item -> SEA: 2, BEA: 1
    Tool call: suggest_transfer -> SUGGESTION (not executed): Move 1 x black boots, size 8, from SEA to POR, for human approval.

## Guardrails

- Suggest-only transfers, never execute. A human approves and does the
  physical transfer.
- Tool inputs are validated in code, and store IDs are normalized
  (e.g. "Seattle" and "SEA" mean the same store).
- Final answers are grounded in tool results. If stock cannot be
  confirmed, the agent says so instead of guessing.
- API key is stored in user-secrets, outside the repo, and is never
  committed.

## Status / What's next

Done so far:
- [x] Fake data model and 3 tools, manually tested
- [x] Claude tool-use loop, working end-to-end
- [x] Tools JSON in a separate file, API key in user-secrets

In progress / next:
- [ ] 10-case evaluation set (in-stock, out-of-stock, transfer available,
      out-of-stock everywhere, size change, ambiguous item, invalid store)
- [ ] Structured logging of tool calls and results
- [ ] More items and stores in fake data
