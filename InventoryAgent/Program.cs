using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;

public record InventoryItem(string StoreId, string Item, int Size, int Quantity);

public class Program
{
    public static string? SqlConnectionString;


    public static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        string apiKey = config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? "";
        // SQL connection string from user-secrets
        SqlConnectionString = config["Sql:ConnectionString"];

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

                    string result = name switch
                    {
                        "check_stock" => CheckStockTool(
                            input["item"]!.ToString(),
                            input["storeId"]!.ToString(),
                            input["size"]!.GetValue<int>()),
                        "find_stores_with_item" => FindStoresWithItemResult(
                            input["item"]!.ToString(),
                            input["size"]!.GetValue<int>()),
                        "suggest_transfer" => SuggestTransfer(
                            input["item"]!.ToString(),
                            input["size"]!.GetValue<int>(),
                            input["from"]!.ToString(),
                            input["to"]!.ToString(),
                            input["quantity"]!.GetValue<int>()),
                        _ => "Unknown tool"
                    };

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
public static string FindStoresWithItemResult(string item, int size)
{
    var found = FindStoresWithItem(item, size);
    if (found.Count == 0)
    {
        return "None";
    }
    return string.Join(", ", found.Select(i => $"{i.StoreId}: {i.Quantity}"));
}

public static string SuggestTransfer(string item, int size, string from, string to, int quantity)
{
    from = NormalizeStoreId(from);
    to = NormalizeStoreId(to);

    return $"SUGGESTION (not executed): Move {quantity} x {item}, " +
        $"size {size}, from {from} to {to}, for human approval.";
}

    public static int CheckStock(string item, string storeId, int size)
    {
        storeId = NormalizeStoreId(storeId);

        using var conn = new SqlConnection(SqlConnectionString);
        conn.Open();

        using var cmd = new SqlCommand(
            "SELECT Quantity FROM Inventory WHERE StoreId = @storeId AND Item = @item AND Size = @size",
            conn);
        cmd.Parameters.AddWithValue("@storeId", storeId);
        cmd.Parameters.AddWithValue("@item", item);
        cmd.Parameters.AddWithValue("@size", size);

        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return 0; // no row for this size = real out-of-stock

        return Convert.ToInt32(result);
    }

    // Wrapper around CheckStock that distinguishes "unknown" from "0 stock"
    // 0 = real store/item, but out of stock -> agent MAY go to find_stores
    // ERROR = unknown store/item -> agent must stop, do not call further tools
    public static string CheckStockTool(string item, string storeIdRaw, int size)
    {
        string storeId = NormalizeStoreId(storeIdRaw);

        // Check store exists in SQL Stores table (was Stores.Any before)
        using (var conn = new SqlConnection(SqlConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM Stores WHERE Id = @storeId", conn);
            cmd.Parameters.AddWithValue("@storeId", storeId);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                return $"ERROR: Unknown store '{storeIdRaw}'. Stop. Do not call any further tools. " +
                    $"Tell the user you cannot find that store and cannot confirm stock.";
            }
        }

        // Check item exists in SQL Inventory table (was Inventory.Any before)
        using (var conn = new SqlConnection(SqlConnectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM Inventory WHERE Item = @item", conn);
            cmd.Parameters.AddWithValue("@item", item);
            if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
            {
                return $"ERROR: Unknown item '{item}'. Stop. Do not call any further tools. " +
                    $"Tell the user you cannot find that item and cannot confirm stock.";
            }
        }

        return CheckStock(item, storeId, size).ToString();
    }

    public static List<InventoryItem> FindStoresWithItem(string item, int size)
    {
        var found = new List<InventoryItem>();

        using var conn = new SqlConnection(SqlConnectionString);
        conn.Open();

        using var cmd = new SqlCommand(
            "SELECT StoreId, Item, Size, Quantity FROM Inventory WHERE Item = @item AND Size = @size AND Quantity > 0",
            conn);
        cmd.Parameters.AddWithValue("@item", item);
        cmd.Parameters.AddWithValue("@size", size);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            found.Add(new InventoryItem(
                reader.GetString(0),  // StoreId
                reader.GetString(1),  // Item
                reader.GetInt32(2),   // Size
                reader.GetInt32(3)    // Quantity
            ));
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
