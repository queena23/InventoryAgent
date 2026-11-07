using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;

public class FindStoresWithItemFunction
{
    [Function("find_stores_with_item")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "find_stores_with_item")] HttpRequest req)
    {
        string item = req.Query["item"]!;
        string sizeStr = req.Query["size"]!;

        if (string.IsNullOrWhiteSpace(item) || !int.TryParse(sizeStr, out int size))
        {
            return new BadRequestObjectResult("Missing or invalid item or size.");
        }

        string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")!;
        var parts = new List<string>();

        using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        using var cmd = new SqlCommand(
            "SELECT StoreId, Quantity FROM Inventory WHERE Item = @item AND Size = @size AND Quantity > 0",
            conn);
        cmd.Parameters.AddWithValue("@item", item);
        cmd.Parameters.AddWithValue("@size", size);

        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            parts.Add($"{reader.GetString(0)}: {reader.GetInt32(1)}");
        }

        string result = parts.Count == 0 ? "None" : string.Join(", ", parts);
        return new OkObjectResult(result);
    }
}
