using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

public record Store(string Id, string Name, string City);
public record InventoryItem(string StoreId, string Item, int Size, int Quantity);

public class Program
{
    private static readonly List<Store> Stores = new()
    {
        new("POR", "Portland", "Portland"),
        new("SEA", "Seattle", "Seattle"),
        new("BEA", "Beaverton", "Beaverton"),
    };

    private static readonly List<InventoryItem> Inventory = new()
    {
        new("POR", "black boots", 8, 0),
        new("POR", "black boots", 9, 1),
        new("SEA", "black boots", 8, 2),
        new("BEA", "black boots", 8, 1),
    };

    public static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>()
            .Build();

        var apiKey = config["Anthropic:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Anthropic API key not found in user-secrets or environment.");
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var toolsJson = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "FakeData", "tools.json"));

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "A customer in seattle wants the black boots in size 8, what can we do?"
            }
        };

        for (int turn = 0; turn < 5; turn++)
        {
            var request = new JsonObject
            {
                ["model"] = "claude-sonnet-4-5",
                ["max_tokens"] = 1000,
                ["system"] = "You are an inventory assistant. Use the tools to check real stock. Only suggest transfers, never execute them. Ground your final answer in tool results.",
                ["messages"] = messages.DeepClone(),
                ["tools"] = JsonNode.Parse(toolsJson)
            };

            var response = await http.PostAsync(
                "https://api.anthropic.com/v1/messages",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API error: {response.StatusCode}");
                Console.WriteLine(body);
                return;
            }

            var json = JsonNode.Parse(body)!.AsObject();
            var content = json["content"]!.AsArray();

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = content.DeepClone()
            });

            var toolResults = new JsonArray();
            var hasToolUse = false;

            foreach (var block in content)
            {
                var type = block!["type"]?.GetValue<string>();
                if (type == "text")
                {
                    Console.WriteLine(block["text"]?.GetValue<string>());
                }
                else if (type == "tool_use")
                {
                    hasToolUse = true;
                    var name = block["name"]!.GetValue<string>();
                    var id = block["id"]!.GetValue<string>();
                    var input = block["input"]!.AsObject();

                    string result = name switch
                    {
                        "check_stock" => CheckStock(
                            input["item"]!.GetValue<string>(),
                            input["storeId"]!.GetValue<string>(),
                            input["size"]!.GetValue<int>()).ToString(),
                        "find_stores_with_item" => string.Join(", ",
                            FindStoresWithItem(
                                input["item"]!.GetValue<string>(),
                                input["size"]!.GetValue<int>())
                            .Select(i => $"{i.StoreId}: {i.Quantity}")),
                        "suggest_transfer" => SuggestTransfer(
                            input["item"]!.GetValue<string>(),
                            input["size"]!.GetValue<int>(),
                            input["from"]!.GetValue<string>(),
                            input["to"]!.GetValue<string>(),
                            input["quantity"]!.GetValue<int>()),
                        _ => "Unknown tool"
                    };

                    Console.WriteLine($"Tool call: {name} -> {result}");

                    toolResults.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = id,
                        ["content"] = result
                    });
                }
            }

            if (!hasToolUse)
            {
                break;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = toolResults
            });
        }
    }

    public static int CheckStock(string item, string storeId, int size)
    {
        // Normalize first, so "Seattle", "SEA", "seattle", "Seatle" all mean SEA
        storeId = NormalizeStoreId(storeId);

        return Inventory.FirstOrDefault(i =>
            i.Item == item && i.StoreId == storeId && i.Size == size)
            ?.Quantity ?? 0;
    }
    public static List<InventoryItem> FindStoresWithItem(string item, int size) =>
        Inventory.Where(i =>
            i.Item == item && i.Size == size && i.Quantity > 0).ToList();

    public static string SuggestTransfer(string item, int size,
        string from, string to, int quantity)
    {
        // Normalize both ends, so a typo in a city name doesn't create a bad suggestion
        from = NormalizeStoreId(from);
        to = NormalizeStoreId(to);

        return $"SUGGESTION (not executed): Move {quantity} x {item}, " +
            $"size {size}, from {from} to {to}, for human approval.";
    }

    // Normalize a store name / ID to a canonical store ID
    // Returns POR, SEA, or BEA if it can be matched, otherwise returns the input as-is
    public static string NormalizeStoreId(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var normalized = input.Trim().ToLowerInvariant();

        // Exact matches first: city names and IDs, case-insensitive
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

        // Fuzzy match for typos: if it's 1-2 edits away from a known city, use that city
        // e.g. "Seatle" -> "Seattle" -> "SEA", "Portand" -> "Portland" -> "POR"
        string[] cities = { "portland", "seattle", "beaverton" };
        var best = cities
            .Select(c => new { City = c, Distance = Levenshtein(normalized, c) })
            .OrderBy(x => x.Distance)
            .First();

        if (best.Distance <= 2)
        {
            return known[best.City];
        }

        // Unknown: return as-is, so CheckStock will return 0 for it
        // In a real product, this is where you'd return an "Unknown store, did you mean...?" error instead
        return input.Trim().ToUpperInvariant();
    }

    // Simple edit-distance (Levenshtein) for typo matching
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

}
