using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public record InventoryItem(string StoreId, string Item, int Size, int Quantity);

public class Program
{
    private const string FunctionsBaseUrl = "https://inventory-agent-func-fxamcvbvb2b8gsae.centralus-01.azurewebsites.net/api";
    private static readonly HttpClient FunctionsHttp = new() { Timeout = TimeSpan.FromSeconds(100) };


    public static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        string apiKey = config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? "";
       

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("Anthropic API key not found in user-secrets or environment.");
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var toolsJson = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "FakeData", "tools.json"));
        var tools = JsonNode.Parse(toolsJson)!.AsArray();

        // Change this string to test a different eval case:
        // Portland size 8, Seattle size 8, Portland size 9, Hawaii size 8, etc.
       
        var cases = EvalCases.All;

        int passed = 0;
        foreach (var test in cases)
        {
            Console.WriteLine($"\n=== {test.Name} ===");
            Console.WriteLine($"User: {test.Message}");

            var toolCalls = await RunAgentAsync(http, tools, test.Message);

            bool pass = toolCalls.SequenceEqual(test.ExpectedTools);
            if (pass) passed++;

            Console.WriteLine($"Expected: {string.Join(", ", test.ExpectedTools)}");
            Console.WriteLine($"Actual:   {string.Join(", ", toolCalls)}");
            Console.WriteLine(pass ? "PASS" : "FAIL");
        }

        Console.WriteLine($"\n=== Summary: {passed}/{cases.Count} passed ===");
        var toolCalls_2 = await RunAgentAsync(http, tools, "Transfer all the black boots from Seattle to Portland right now");
        Console.WriteLine($"Tools used: {string.Join(", ", toolCalls_2)}");

    }

    public static async Task<List<string>> RunAgentAsync(HttpClient http, JsonArray tools, string userMessage)
    {
        var toolCalls = new List<string>();

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = userMessage
            }
        };

        string system = "You are an inventory assistant. Use tools to check stock. " +
            "Suggest transfers only, never execute them. " +
            "Ground every answer in tool results. " +
            "If a tool returns an ERROR about an unknown store or item, stop. " +
            "Do not call any further tools. Tell the user you cannot confirm stock.";

        for (int turn = 0; turn < 5; turn++)
        {
            var request = new JsonObject
            {
                ["model"] = "claude-sonnet-4-5",
                ["max_tokens"] = 1000,
                ["system"] = system,
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone()
            };

            var response = await http.PostAsync(
                "https://api.anthropic.com/v1/messages",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            if (json["error"] is not null)
            {
                Console.WriteLine($"API error: {json["error"]!["type"]}");
                Console.WriteLine(json["error"]!.ToJsonString());
                break;
            }

            var content = json["content"]!.AsArray();
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

            bool madeToolCall = false;

            foreach (var block in content)
            {
                if (block!["type"]?.ToString() == "text")
                {
                    Console.WriteLine(block!["text"]!.ToString());
                }
                else if (block!["type"]?.ToString() == "tool_use")
                {
                    madeToolCall = true;
                    string name = block!["name"]!.ToString();
                    string id = block!["id"]!.ToString();
                    var input = block!["input"]!.AsObject();

                    toolCalls.Add(name);

                    string result;
                    switch (name)
                    {
                        case "check_stock":
                            result = await CheckStockTool(
                                input["item"]!.ToString(),
                                input["storeId"]!.ToString(),
                                input["size"]!.GetValue<int>());
                            break;
                        case "find_stores_with_item":
                            result = await FindStoresWithItemResult(
                                input["item"]!.ToString(),
                                input["size"]!.GetValue<int>());
                            break;
                        case "suggest_transfer":
                            result = await SuggestTransfer(
                                input["item"]!.ToString(),
                                input["size"]!.GetValue<int>(),
                                input["from"]!.ToString(),
                                input["to"]!.ToString(),
                                input["quantity"]!.GetValue<int>());
                            break;
                        default:
                            result = "Unknown tool";
                            break;
                    }


                    Console.WriteLine($"Tool call: {name} -> {result}");
                    LogToolCall(name, input, result);

                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = id,
                                ["content"] = result
                            }
                        }
                    });
                }
            }

            if (!madeToolCall)
            {
                break;
            }
        }

        return toolCalls;
    }
    private static async Task<string> GetFromFunctionAsync(string url)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await FunctionsHttp.GetAsync(url);
                string body = (await response.Content.ReadAsStringAsync()).Trim();
                if (response.IsSuccessStatusCode)
                {
                    return body;
                }
                // Non-success (e.g. 500 on first wake-up) — retry once
                if (attempt == 0) continue;
                return body;
            }
            catch (Exception) when (attempt == 0)
            {
                // Timeout / connection error on first try — retry once
                continue;
            }
        }
        return "ERROR: Could not reach the function. Stop. Do not call any further tools.";
    }

    public static async Task<string> FindStoresWithItemResult(string item, int size)
    {
        string url = $"{FunctionsBaseUrl}/find_stores_with_item?item={Uri.EscapeDataString(item)}&size={size}";
        return await GetFromFunctionAsync(url);
    }

    public static async Task<string> SuggestTransfer(string item, int size, string from, string to, int quantity)
    {
        string url = $"{FunctionsBaseUrl}/suggest_transfer?item={Uri.EscapeDataString(item)}&size={size}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}&quantity={quantity}";
        return await GetFromFunctionAsync(url);
    }

    public static async Task<int> CheckStock(string item, string storeId, int size)
    {
        string result = await CheckStockTool(item, storeId, size);
        return int.TryParse(result, out int qty) ? qty : 0;
    }

    // Wrapper around CheckStock that distinguishes "unknown" from "0 stock"
    // 0 = real store/item, but out of stock -> agent MAY go to find_stores
    // ERROR = unknown store/item -> agent must stop, do not call further tools
    // The Function already does the store/item existence checks and returns
    // the exact ERROR text, so this just passes its response through.
    public static async Task<string> CheckStockTool(string item, string storeIdRaw, int size)
    {
        string url = $"{FunctionsBaseUrl}/check_stock?item={Uri.EscapeDataString(item)}&storeId={Uri.EscapeDataString(storeIdRaw)}&size={size}";
        return await GetFromFunctionAsync(url);
    }

    public static async Task<List<InventoryItem>> FindStoresWithItem(string item, int size)
    {
        string result = await FindStoresWithItemResult(item, size);
        var found = new List<InventoryItem>();
        if (string.IsNullOrWhiteSpace(result) || result == "None" || result.StartsWith("ERROR"))
        {
            return found;
        }
        // Function returns "SEA: 2, BEA: 1"
        foreach (var part in result.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(": ", StringSplitOptions.RemoveEmptyEntries);
            if (kv.Length == 2 && int.TryParse(kv[1], out int qty))
            {
                found.Add(new InventoryItem(kv[0].Trim(), item, size, qty));
            }
        }
        return found;
    }

    // Normalize a store name / ID to a canonical store ID
    // "Seattle", "SEA", "seattle", "Seatle" all mean SEA
    public static string NormalizeStoreId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var normalized = input.Trim().ToLowerInvariant();

        var known = new Dictionary<string, string>
        {
            ["portland"] = "POR",
            ["por"] = "POR",
            ["seattle"] = "SEA",
            ["sea"] = "SEA",
            ["beaverton"] = "BEA",
            ["bea"] = "BEA",
        };

        if (known.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        // Fuzzy match for typos: 1-2 edits away from a known city
        string[] cities = { "portland", "seattle", "beaverton" };
        var best = cities
            .Select(c => new { City = c, Distance = Levenshtein(normalized, c) })
            .OrderBy(x => x.Distance)
            .First();

        if (best.Distance <= 2)
        {
            return known[best.City];
        }

        return input.Trim().ToUpperInvariant();
    }

    public static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    } 
    public static void LogToolCall(string toolName, JsonObject input, string result)
    {
        Directory.CreateDirectory("logs");

        var log = new JsonObject
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["tool"] = toolName,
            ["input"] = input.DeepClone(),
            ["result"] = result
        };

        File.AppendAllText(
            Path.Combine("logs", "tool-calls.jsonl"),
            log.ToJsonString() + Environment.NewLine);
    }

}
