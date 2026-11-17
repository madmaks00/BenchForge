namespace BenchForge.Contracts;

public static class WorkloadTemplates
{
    private static ValueDefinition Id() => new() { Name = "id", PrimaryKey = true };
    private static ValueDefinition Ref(string name, string table) => new() { Name = name, Generator = ValueGenerator.Reference, Reference = table };
    private static SqlStep Read(string sql, long? min = null, long? max = null) => new() { Command = new() { Sql = sql }, MinRows = min, MaxRows = max };
    private static SqlStep Write(string sql) => new() { Command = new() { Sql = sql }, Result = SqlResultMode.NonQuery, MinRows = 1, MaxRows = 1 };

    public static SqlWorkload Store() => new()
    {
        Name = "Интернет-магазин", Description = "Покупатели, товары, заказы и позиции. Чтение карточки, история заказов и создание заказа в транзакции.",
        Datasets =
        [
            new() { Name = "customers", Columns = [Id(), new() { Name = "email", Type = SqlValueType.Text, Generator = ValueGenerator.Text, Prefix = "customer-{row}@example.test", Length = 0, Unique = true },
                new() { Name = "name", Type = SqlValueType.Text, Generator = ValueGenerator.Text, Prefix = "Customer ", Length = 24 }] },
            new() { Name = "products", FixedRows = 100, Columns = [Id(), new() { Name = "title", Type = SqlValueType.Text, Generator = ValueGenerator.Text, Length = 48 },
                new() { Name = "price", Type = SqlValueType.Decimal, Generator = ValueGenerator.Decimal, Min = 10, Max = 10000, Scale = 2 }] },
            new() { Name = "orders", RowsMultiplier = 2, Columns = [Id(), Ref("customer_id", "customers"),
                new() { Name = "status", Type = SqlValueType.Text, Generator = ValueGenerator.Choice, Choices = ["new", "paid", "shipped", "done"] }] },
            new() { Name = "order_items", RowsMultiplier = 4, Columns = [Id(), Ref("order_id", "orders"), Ref("product_id", "products"),
                new() { Name = "quantity", Type = SqlValueType.Int32, Generator = ValueGenerator.Integer, Min = 1, Max = 5 }] }
        ],
        AfterLoad = new() { Sql = "CREATE INDEX orders_customer_idx ON orders(customer_id); CREATE INDEX items_order_idx ON order_items(order_id);" },
        Operations =
        [
            new() { Id = "customer", Name = "Карточка покупателя", Weight = 50, Parameters = [Ref("customer_id", "customers")],
                Steps = [Read("SELECT id,email,name FROM customers WHERE id=@customer_id", 1, 1)] },
            new() { Id = "history", Name = "История заказов с товарами", Weight = 40, Parameters = [Ref("customer_id", "customers")],
                Steps = [Read("SELECT o.id,o.status,p.title,i.quantity FROM orders o JOIN order_items i ON i.order_id=o.id JOIN products p ON p.id=i.product_id WHERE o.customer_id=@customer_id ORDER BY o.id LIMIT 50", 0, 50) with { Command = new() { Sql = "SELECT o.id,o.status,p.title,i.quantity FROM orders o JOIN order_items i ON i.order_id=o.id JOIN products p ON p.id=i.product_id WHERE o.customer_id=@customer_id ORDER BY o.id LIMIT 50",
                    Overrides = new() { [TargetKind.SqlServer] = "SELECT TOP (50) o.id,o.status,p.title,i.quantity FROM orders o JOIN order_items i ON i.order_id=o.id JOIN products p ON p.id=i.product_id WHERE o.customer_id=@customer_id ORDER BY o.id", [TargetKind.Oracle] = "SELECT o.id,o.status,p.title,i.quantity FROM orders o JOIN order_items i ON i.order_id=o.id JOIN products p ON p.id=i.product_id WHERE o.customer_id=@customer_id ORDER BY o.id FETCH FIRST 50 ROWS ONLY" } } }] },
            new() { Id = "checkout", Name = "Создание заказа", Weight = 10, ReadOnly = false, Transaction = true,
                Parameters = [Ref("customer_id", "customers"), Ref("product_id", "products")],
                Steps = [Write("INSERT INTO orders(id,customer_id,status) VALUES(@rows_orders+@sequence,@customer_id,'new')"),
                    Write("INSERT INTO order_items(id,order_id,product_id,quantity) VALUES(@rows_order_items+@sequence,@rows_orders+@sequence,@product_id,1)")] }
        ],
        Checks = [new() { Name = "Все заказы принадлежат покупателям", Command = new() { Sql = "SELECT COUNT(*) FROM orders o LEFT JOIN customers c ON c.id=o.customer_id WHERE c.id IS NULL" } },
            new() { Name = "Все позиции принадлежат заказам", Command = new() { Sql = "SELECT COUNT(*) FROM order_items i LEFT JOIN orders o ON o.id=i.order_id WHERE o.id IS NULL" } }]
    };

    public static SqlWorkload Wallets() => new()
    {
        Name = "Кошельки и переводы", Description = "Транзакция переводит единицу между двумя счетами. Контроль суммы обнаруживает потерю или неполный перевод.",
        Datasets = [new() { Name = "wallets", Columns = [Id(), new() { Name = "balance", Generator = ValueGenerator.Constant, Constant = "10000" }] }],
        Operations =
        [
            new() { Id = "balance", Name = "Посмотреть баланс", Weight = 80, Steps = [Read("SELECT id,balance FROM wallets WHERE id=@key", 1, 1)] },
            new() { Id = "transfer", Name = "Перевод между счетами", Weight = 20, ReadOnly = false, Transaction = true,
                Steps = [Write("UPDATE wallets SET balance=balance-1 WHERE id=@key"), Write("UPDATE wallets SET balance=balance+1 WHERE id=(@key % @baseRows)+1") with { Command = new() { Sql = "UPDATE wallets SET balance=balance+1 WHERE id=(@key % @baseRows)+1", Overrides = new() { [TargetKind.Oracle] = "UPDATE wallets SET balance=balance+1 WHERE id=MOD(@key,@baseRows)+1" } } }] }
        ],
        Checks = [new() { Name = "Сумма денег сохранилась", Command = new() { Sql = "SELECT SUM(balance)-COUNT(*)*10000 FROM wallets" } }]
    };

    public static SqlWorkload Blank() => new()
    {
        Name = "Мой сценарий", Datasets = [new() { Name = "items", Columns = [Id(), new() { Name = "title", Type = SqlValueType.Text, Generator = ValueGenerator.Text, Length = 64 }] }],
        Operations = [new() { Id = "read", Name = "Найти запись", Steps = [Read("SELECT id,title FROM items WHERE id=@key", 1, 1)] }],
        Checks = [new() { Name = "Исходное число записей", Command = new() { Sql = "SELECT COUNT(*) FROM items" }, Expected = "rows:items", After = false }]
    };
}
