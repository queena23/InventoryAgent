using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

public class SuggestTransferFunction
{
    [Function("suggest_transfer")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "suggest_transfer")] HttpRequest req)
    {
        string item = req.Query["item"]!;
        string sizeStr = req.Query["size"]!;
        string fromRaw = req.Query["from"]!;
        string toRaw = req.Query["to"]!;
        string qtyStr = req.Query["quantity"]!;

        if (string.IsNullOrWhiteSpace(item) || !int.TryParse(sizeStr, out int size) ||
            string.IsNullOrWhiteSpace(fromRaw) || string.IsNullOrWhiteSpace(toRaw) ||
            !int.TryParse(qtyStr, out int quantity))
        {
            return new BadRequestObjectResult("Missing or invalid item, size, from, to, or quantity.");
        }

        string from = InventoryHelpers.NormalizeStoreId(fromRaw);
        string to = InventoryHelpers.NormalizeStoreId(toRaw);

        string result = $"SUGGESTION (not executed): Move {quantity} x {item}, size {size}, from {from} to {to}, for human approval.";
        return new OkObjectResult(result);
    }
}
