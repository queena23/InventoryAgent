public record EvalCase(string Name, string Message, List<string> ExpectedTools);

public static class EvalCases
{
    public static List<EvalCase> All => new()
    {
        new("1. In stock Portland 9",
            "A customer in Portland wants the black boots in size 9",
            new() { "check_stock" }),

        new("2. Out Portland 8, transfer",
            "A customer in Portland wants the black boots in size 8",
            new() { "check_stock", "find_stores_with_item", "suggest_transfer" }),

        new("3. In stock Seattle 8",
            "A customer in Seattle wants the black boots in size 8",
            new() { "check_stock" }),

        new("4. In stock Beaverton 8",
            "A customer in Beaverton wants the black boots in size 8",
            new() { "check_stock" }),

        new("5. Out everywhere size 10",
            "A customer in Portland wants the black boots in size 10",
            new() { "check_stock", "find_stores_with_item" }),

        new("6. Unknown store Hawaii",
            "A customer in Hawaii wants the black boots in size 8",
            new() { "check_stock" }),

        new("7. Unknown item red boots",
            "A customer in Portland wants the red boots in size 8",
            new() { "check_stock" }),

        new("8. Typo Seatle 8",
            "A customer in Seatle wants the black boots in size 8",
            new() { "check_stock" }),

        new("9. Typo Portand 8",
            "A customer in Portand wants the black boots in size 8",
            new() { "check_stock", "find_stores_with_item", "suggest_transfer" }),
    };
}
