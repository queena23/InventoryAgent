using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

public class CheckStockFunction
{
    private readonly ILogger<CheckStockFunction> _logger;

    public CheckStockFunction(ILogger<CheckStockFunction> logger)
    {
        _logger = logger;
    }

    [Function("check_stock")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "check_stock")] HttpRequest req)
    {
        string item = req.Query["item"]!;
        string storeIdRaw = req.Query["storeId"]!;
        string sizeStr = req.Query["size"]!;

        if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(storeIdRaw) ||
            !int.TryParse(sizeStr, out int size))
        {
            return new BadRequestObjectResult("Missing or invalid item, storeId, or size.");
        }

        string storeId = InventoryHelpers.NormalizeStoreId(storeIdRaw);
        string connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")!;

        // Check store exists
        using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM Stores WHERE Id = @storeId", conn);
            cmd.Parameters.AddWithValue("@storeId", storeId);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
            {
                return new OkObjectResult($"ERROR: Unknown store '{storeIdRaw}'. Stop. Do not call any further tools. Tell the user you cannot find that store and cannot confirm stock.");
            }
        }

        // Check item exists
        using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM Inventory WHERE Item = @item", conn);
            cmd.Parameters.AddWithValue("@item", item);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
            {
                return new OkObjectResult($"ERROR: Unknown item '{item}'. Stop. Do not call any further tools. Tell the user you cannot find that item and cannot confirm stock.");
            }
        }

        // Get quantity
        using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT Quantity FROM Inventory WHERE StoreId = @storeId AND Item = @item AND Size = @size", conn);
            cmd.Parameters.AddWithValue("@storeId", storeId);
            cmd.Parameters.AddWithValue("@item", item);
            cmd.Parameters.AddWithValue("@size", size);

            var result = await cmd.ExecuteScalarAsync();
            int qty = (result == null || result == DBNull.Value) ? 0 : Convert.ToInt32(result);
            return new OkObjectResult(qty.ToString());
        }
    }
}
