using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using BDOLootTracker.Models;
using Microsoft.Data.Sqlite;

namespace BDOLootTracker.Services;

public sealed class DatabaseService
{
    private string _databasePath;
    private readonly ConcurrentDictionary<(uint ItemId, string Region, string Language), ItemDefinition> _cache = new();

    public DatabaseService(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    public string DatabasePath => _databasePath;

    public void ChangeDatabase(string databasePath)
    {
        if (string.Equals(_databasePath, databasePath, StringComparison.OrdinalIgnoreCase))
            return;

        _databasePath = databasePath;
        _cache.Clear();
        Initialize();
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _databasePath
    }.ToString();

    private void Initialize()
    {
        var folder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS Items (
                ItemId            INTEGER PRIMARY KEY,
                Name              TEXT NOT NULL DEFAULT '',
                IconUrl           TEXT NULL,
                LocalIconPath     TEXT NULL,
                IsTrash           INTEGER NOT NULL DEFAULT 0,
                IsRare            INTEGER NOT NULL DEFAULT 0,
                UpdatedAtUtc      TEXT NULL,
                Grade             INTEGER NOT NULL DEFAULT 0,
                CategoryPrimary   INTEGER NOT NULL DEFAULT 0,
                CategorySecondary INTEGER NOT NULL DEFAULT 0,
                VendorPrice       INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS ItemNames (
                ItemId   INTEGER NOT NULL,
                Language TEXT NOT NULL,
                Name     TEXT NOT NULL,
                PRIMARY KEY (ItemId, Language)
            );

            CREATE TABLE IF NOT EXISTS ItemPrices (
                ItemId       INTEGER NOT NULL,
                Region       TEXT NOT NULL,
                UnitPrice    INTEGER NOT NULL DEFAULT 0,
                CurrentStock INTEGER NOT NULL DEFAULT 0,
                TotalTrades  INTEGER NOT NULL DEFAULT 0,
                UpdatedAtUtc TEXT NULL,
                PRIMARY KEY (ItemId, Region)
            );

            CREATE TABLE IF NOT EXISTS Metadata (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Sessions (
                SessionId       INTEGER PRIMARY KEY AUTOINCREMENT,
                StartedAtUtc    TEXT NOT NULL,
                EndedAtUtc      TEXT NULL,
                LastSavedAtUtc  TEXT NULL,
                Region          TEXT NOT NULL,
                CharacterName   TEXT NULL,
                ClassType       INTEGER NULL,
                ClassName       TEXT NULL,
                Spec            TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS SessionLoot (
                SessionId INTEGER NOT NULL,
                ItemId    INTEGER NOT NULL,
                Quantity  INTEGER NOT NULL,
                ItemName  TEXT NOT NULL DEFAULT '',
                UnitPrice INTEGER NOT NULL DEFAULT 0,
                IsTrash   INTEGER NOT NULL DEFAULT 0,
                IconPath  TEXT NULL,
                PRIMARY KEY (SessionId, ItemId)
            );

            CREATE TABLE IF NOT EXISTS IgnoredItems (
                ItemId      INTEGER PRIMARY KEY,
                Name        TEXT NOT NULL DEFAULT '',
                AddedAtUtc  TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Classes (
                ClassType   INTEGER PRIMARY KEY,
                Name        TEXT NOT NULL,
                IconUrl     TEXT NULL,
                IconPath    TEXT NULL,
                SortOrder   INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS ClassSpecs (
                ClassType   INTEGER NOT NULL,
                Spec        TEXT NOT NULL,
                SortOrder   INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (ClassType, Spec)
            );

            CREATE TABLE IF NOT EXISTS GrindSpots (
                SpotKey      TEXT PRIMARY KEY,
                Name         TEXT NOT NULL,
                UpdatedAtUtc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS GrindSpotDrops (
                SpotKey TEXT NOT NULL,
                ItemId  INTEGER NOT NULL,
                PRIMARY KEY (SpotKey, ItemId)
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "Items", "IsRare", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Items", "Grade", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Items", "CategoryPrimary", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Items", "CategorySecondary", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Items", "VendorPrice", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "ItemPrices", "CurrentStock", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "ItemPrices", "TotalTrades", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Sessions", "LastSavedAtUtc", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "ClassType", "INTEGER NULL");
        EnsureColumn(connection, "Sessions", "ClassName", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "Spec", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "SpotKey", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "SpotName", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "GarmothUploadedAtUtc", "TEXT NULL");
        EnsureColumn(connection, "Sessions", "GarmothUploadCount", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "Sessions", "DropRatePercent", "INTEGER NULL");
        EnsureColumn(connection, "SessionLoot", "ItemName", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "SessionLoot", "UnitPrice", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "SessionLoot", "IsTrash", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "SessionLoot", "IconPath", "TEXT NULL");

        SeedKnownItems(connection);
        SeedCharacterClasses(connection);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        bool exists = false;

        using (var check = connection.CreateCommand())
        {
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();

            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
            return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void SeedKnownItems(SqliteConnection connection)
    {
        var items = new (uint Id, string Name)[]
        {
            (1, "Silver"),
            (513, "Instant HP Potion (Small)"),
            (44291, "Broken Wooden Fragment"),
            (44814, "Altar Imp's Trumpet"),
            (721002, "Ancient Spirit Dust"),
            (721003, "Caphras Stone")
        };

        foreach (var item in items)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO Items(ItemId, Name, IconUrl, UpdatedAtUtc)
                VALUES ($id, $name, $icon, $updated);

                INSERT OR IGNORE INTO ItemNames(ItemId, Language, Name)
                VALUES ($id, 'us', $name);
                """;
            cmd.Parameters.AddWithValue("$id", (long)item.Id);
            cmd.Parameters.AddWithValue("$name", item.Name);
            cmd.Parameters.AddWithValue("$icon", BuildPrimaryIconUrl(item.Id));
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        using var silverPrice = connection.CreateCommand();
        silverPrice.CommandText = """
            INSERT OR IGNORE INTO ItemPrices(ItemId, Region, UnitPrice, UpdatedAtUtc)
            VALUES (1, 'EU', 1, $updated),
                   (1, 'NA', 1, $updated),
                   (1, 'SEA', 1, $updated);
            """;
        silverPrice.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        silverPrice.ExecuteNonQuery();
    }

    private static void SeedCharacterClasses(SqliteConnection connection)
    {
        var classes = new (int ClassType, string Name, int SortOrder, string[] Specs)[]
        {
            (0,  "Warrior",      0,  new[] { "Succession", "Awakening" }),
            (4,  "Ranger",       1,  new[] { "Succession", "Awakening" }),
            (8,  "Sorceress",    2,  new[] { "Succession", "Awakening" }),
            (12, "Berserker",    3,  new[] { "Succession", "Awakening" }),
            (16, "Tamer",        4,  new[] { "Succession", "Awakening" }),
            (20, "Musa",         5,  new[] { "Succession", "Awakening" }),
            (21, "Maehwa",       6,  new[] { "Succession", "Awakening" }),
            (24, "Valkyrie",     7,  new[] { "Succession", "Awakening" }),
            (25, "Kunoichi",     8,  new[] { "Succession", "Awakening" }),
            (26, "Ninja",        9,  new[] { "Succession", "Awakening" }),
            (28, "Wizard",      10,  new[] { "Succession", "Awakening" }),
            (31, "Witch",       11,  new[] { "Succession", "Awakening" }),
            (27, "Dark Knight", 12,  new[] { "Succession", "Awakening" }),
            (19, "Striker",     13,  new[] { "Succession", "Awakening" }),
            (23, "Mystic",      14,  new[] { "Succession", "Awakening" }),
            (11, "Lahn",        15,  new[] { "Succession", "Awakening" }),
            (29, "Archer",      16,  new[] { "Ascension" }),
            (17, "Shai",        17,  new[] { "Talent" }),
            (5,  "Guardian",    18,  new[] { "Succession", "Awakening" }),
            (1,  "Hashashin",   19,  new[] { "Succession", "Awakening" }),
            (9,  "Nova",        20,  new[] { "Succession", "Awakening" }),
            (2,  "Sage",        21,  new[] { "Succession", "Awakening" }),
            (10, "Corsair",     22,  new[] { "Succession", "Awakening" }),
            (7,  "Drakania",    23,  new[] { "Succession", "Awakening" }),
            (30, "Woosa",       24,  new[] { "Succession", "Awakening" }),
            (15, "Maegu",       25,  new[] { "Succession", "Awakening" }),
            (6,  "Scholar",     26,  new[] { "Ascension" }),
            (33, "Dosa",        27,  new[] { "Succession", "Awakening" }),
            (34, "Deadeye",     28,  new[] { "Ascension" }),
            (3,  "Wukong",      29,  new[] { "Ascension" }),
            (32, "Seraph",      30,  new[] { "Ascension" }),
            (35, "Agent",       31,  new[] { "Succession" })
        };

        using var transaction = connection.BeginTransaction();

        foreach (var item in classes)
        {
            using (var classCommand = connection.CreateCommand())
            {
                classCommand.Transaction = transaction;
                classCommand.CommandText = """
                    INSERT INTO Classes(ClassType, Name, IconUrl, SortOrder)
                    VALUES ($type, $name, $icon, $sort)
                    ON CONFLICT(ClassType)
                    DO UPDATE SET
                        Name = excluded.Name,
                        IconUrl = excluded.IconUrl,
                        SortOrder = excluded.SortOrder;
                    """;
                classCommand.Parameters.AddWithValue("$type", item.ClassType);
                classCommand.Parameters.AddWithValue("$name", item.Name);
                classCommand.Parameters.AddWithValue("$icon", BuildOfficialClassIconUrl(item.ClassType));
                classCommand.Parameters.AddWithValue("$sort", item.SortOrder);
                classCommand.ExecuteNonQuery();
            }

            // The shipped definitions are the source of truth for which specs are selectable.
            using (var clearSpecs = connection.CreateCommand())
            {
                clearSpecs.Transaction = transaction;
                clearSpecs.CommandText = "DELETE FROM ClassSpecs WHERE ClassType = $type;";
                clearSpecs.Parameters.AddWithValue("$type", item.ClassType);
                clearSpecs.ExecuteNonQuery();
            }

            for (int i = 0; i < item.Specs.Length; i++)
            {
                using var specCommand = connection.CreateCommand();
                specCommand.Transaction = transaction;
                specCommand.CommandText = """
                    INSERT INTO ClassSpecs(ClassType, Spec, SortOrder)
                    VALUES ($type, $spec, $sort);
                    """;
                specCommand.Parameters.AddWithValue("$type", item.ClassType);
                specCommand.Parameters.AddWithValue("$spec", item.Specs[i]);
                specCommand.Parameters.AddWithValue("$sort", i);
                specCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    public static string BuildOfficialClassIconUrl(int classType)
        => $"https://static.pearlcdn.com/asset/brand/bdo/contents_bdo/img/classes/class_{classType}/class_detail_top_symbol.png";

    public IReadOnlyList<CharacterClassOption> GetCharacterClasses()
    {
        var result = new List<CharacterClassOption>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        var specsByClass = new Dictionary<int, List<string>>();
        using (var specCommand = connection.CreateCommand())
        {
            specCommand.CommandText = """
                SELECT ClassType, Spec
                FROM ClassSpecs
                ORDER BY ClassType, SortOrder, Spec;
                """;

            using var reader = specCommand.ExecuteReader();
            while (reader.Read())
            {
                int classType = reader.GetInt32(0);
                if (!specsByClass.TryGetValue(classType, out var list))
                {
                    list = new List<string>();
                    specsByClass[classType] = list;
                }

                list.Add(reader.GetString(1));
            }
        }

        using var classCommand = connection.CreateCommand();
        classCommand.CommandText = """
            SELECT ClassType, Name, IconUrl, IconPath, SortOrder
            FROM Classes
            ORDER BY SortOrder, Name;
            """;

        using var classReader = classCommand.ExecuteReader();
        while (classReader.Read())
        {
            int classType = classReader.GetInt32(0);
            result.Add(new CharacterClassOption
            {
                ClassType = classType,
                Name = classReader.GetString(1),
                IconUrl = classReader.IsDBNull(2) ? null : classReader.GetString(2),
                IconPath = classReader.IsDBNull(3) ? null : classReader.GetString(3),
                SortOrder = classReader.GetInt32(4),
                Specs = specsByClass.TryGetValue(classType, out var specs)
                    ? specs.ToArray()
                    : Array.Empty<string>()
            });
        }

        return result;
    }

    public void UpdateClassIconPath(int classType, string? iconPath)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Classes SET IconPath = $path WHERE ClassType = $type;";
        command.Parameters.AddWithValue("$type", classType);
        command.Parameters.AddWithValue("$path", string.IsNullOrWhiteSpace(iconPath) ? DBNull.Value : iconPath);
        command.ExecuteNonQuery();
    }

    public static string BuildPrimaryIconUrl(uint itemId)
        => $"https://s1.pearlcdn.com/NAEU/TradeMarket/Common/img/BDO/item/{itemId}.png";

    public static string BuildGarmothFallbackIconUrl(uint itemId)
        => $"https://assets.garmoth.com/img/new_icon/03_etc/04_dropitem/{itemId:D8}.webp";

    public string? GetIconUrl(uint itemId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IconUrl FROM Items WHERE ItemId = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", (long)itemId);

        object? value = command.ExecuteScalar();
        if (value == null || value == DBNull.Value)
            return null;

        string url = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return string.IsNullOrWhiteSpace(url) ? null : url;
    }

    public ItemDefinition GetItem(uint itemId, string region, string language)
    {
        region = NormalizeRegion(region);
        language = NormalizeLanguage(language);
        var key = (itemId, region, language);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(
                    selectedName.Name,
                    englishName.Name,
                    i.Name,
                    'Unknown Item #' || i.ItemId
                ) AS DisplayName,
                i.IconUrl,
                i.LocalIconPath,
                i.IsTrash,
                i.Grade,
                COALESCE(NULLIF(p.UnitPrice, 0), i.VendorPrice, 0)
            FROM Items i
            LEFT JOIN ItemNames selectedName
              ON selectedName.ItemId = i.ItemId AND selectedName.Language = $language
            LEFT JOIN ItemNames englishName
              ON englishName.ItemId = i.ItemId AND englishName.Language = 'us'
            LEFT JOIN ItemPrices p
              ON p.ItemId = i.ItemId AND p.Region = $region
            WHERE i.ItemId = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", (long)itemId);
        command.Parameters.AddWithValue("$region", region);
        command.Parameters.AddWithValue("$language", language);

        using var reader = command.ExecuteReader();

        ItemDefinition result;
        if (reader.Read())
        {
            result = new ItemDefinition
            {
                ItemId = itemId,
                Name = reader.GetString(0),
                IconUrl = reader.IsDBNull(1) ? BuildPrimaryIconUrl(itemId) : reader.GetString(1),
                LocalIconPath = reader.IsDBNull(2) ? null : reader.GetString(2),
                IsTrash = reader.GetInt64(3) != 0,
                Grade = reader.GetInt32(4),
                UnitPrice = reader.GetInt64(5)
            };
        }
        else
        {
            result = new ItemDefinition
            {
                ItemId = itemId,
                Name = $"Unknown Item #{itemId}",
                IconUrl = BuildPrimaryIconUrl(itemId),
                UnitPrice = itemId == 1 ? 1 : 0,
                IsTrash = false,
                Grade = 0
            };

            // Remember actually-seen unknown IDs locally. This does not trigger
            // any network request; it only makes them available to the next
            // explicit Database Fetch / Update fallback pass.
            reader.Close();
            using var remember = connection.CreateCommand();
            remember.CommandText = """
                INSERT OR IGNORE INTO Items(ItemId, Name, IconUrl, UpdatedAtUtc)
                VALUES ($id, $name, $icon, $updated);
                """;
            remember.Parameters.AddWithValue("$id", (long)itemId);
            remember.Parameters.AddWithValue("$name", $"Unknown Item #{itemId}");
            remember.Parameters.AddWithValue("$icon", BuildPrimaryIconUrl(itemId));
            remember.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            remember.ExecuteNonQuery();
        }

        _cache[key] = result;
        return result;
    }

    public void UpsertItemCatalog(IReadOnlyCollection<CatalogItemRecord> items, DateTime fetchedAtUtc)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using var itemCommand = connection.CreateCommand();
        itemCommand.Transaction = transaction;
        itemCommand.CommandText = """
            INSERT INTO Items(
                ItemId, Name, IconUrl, Grade,
                CategoryPrimary, CategorySecondary, UpdatedAtUtc)
            VALUES(
                $id, $name, $icon, $grade,
                $primary, $secondary, $updated)
            ON CONFLICT(ItemId) DO UPDATE SET
                Name = excluded.Name,
                IconUrl = excluded.IconUrl,
                Grade = excluded.Grade,
                CategoryPrimary = excluded.CategoryPrimary,
                CategorySecondary = excluded.CategorySecondary,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        var pId = itemCommand.Parameters.Add("$id", SqliteType.Integer);
        var pName = itemCommand.Parameters.Add("$name", SqliteType.Text);
        var pIcon = itemCommand.Parameters.Add("$icon", SqliteType.Text);
        var pGrade = itemCommand.Parameters.Add("$grade", SqliteType.Integer);
        var pPrimary = itemCommand.Parameters.Add("$primary", SqliteType.Integer);
        var pSecondary = itemCommand.Parameters.Add("$secondary", SqliteType.Integer);
        var pUpdated = itemCommand.Parameters.Add("$updated", SqliteType.Text);

        using var nameCommand = connection.CreateCommand();
        nameCommand.Transaction = transaction;
        nameCommand.CommandText = """
            INSERT INTO ItemNames(ItemId, Language, Name)
            VALUES ($id, $language, $name)
            ON CONFLICT(ItemId, Language) DO UPDATE SET
                Name = excluded.Name;
            """;

        var nId = nameCommand.Parameters.Add("$id", SqliteType.Integer);
        var nLanguage = nameCommand.Parameters.Add("$language", SqliteType.Text);
        var nName = nameCommand.Parameters.Add("$name", SqliteType.Text);

        string updatedText = fetchedAtUtc.ToString("O");

        foreach (var item in items)
        {
            string defaultName = item.Names.TryGetValue("us", out var english)
                ? english
                : item.Names.Values.FirstOrDefault() ?? $"Item #{item.ItemId}";

            pId.Value = (long)item.ItemId;
            pName.Value = defaultName;
            pIcon.Value = BuildPrimaryIconUrl(item.ItemId);
            pGrade.Value = item.Grade;
            pPrimary.Value = item.CategoryPrimary;
            pSecondary.Value = item.CategorySecondary;
            pUpdated.Value = updatedText;
            itemCommand.ExecuteNonQuery();

            foreach (var pair in item.Names)
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                    continue;

                nId.Value = (long)item.ItemId;
                nLanguage.Value = NormalizeLanguage(pair.Key);
                nName.Value = pair.Value;
                nameCommand.ExecuteNonQuery();
            }
        }

        SetMetadata(connection, transaction, "catalog_updated_utc", updatedText);
        transaction.Commit();
        _cache.Clear();
    }

    public void UpsertMarketPrices(IReadOnlyCollection<MarketPriceRecord> prices, string region, DateTime fetchedAtUtc)
    {
        region = NormalizeRegion(region);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using var ensureItem = connection.CreateCommand();
        ensureItem.Transaction = transaction;
        ensureItem.CommandText = """
            INSERT INTO Items(
                ItemId, Name, IconUrl, IsTrash, IsRare, UpdatedAtUtc)
            VALUES(
                $id, $name, $icon, $trash, $rare, $updated)
            ON CONFLICT(ItemId) DO UPDATE SET
                Name = CASE
                    WHEN excluded.Name <> '' THEN excluded.Name
                    ELSE Items.Name
                END,
                IconUrl = CASE
                    WHEN excluded.IconUrl <> '' THEN excluded.IconUrl
                    ELSE Items.IconUrl
                END,
                IsTrash = excluded.IsTrash,
                IsRare = excluded.IsRare,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        var eId = ensureItem.Parameters.Add("$id", SqliteType.Integer);
        var eName = ensureItem.Parameters.Add("$name", SqliteType.Text);
        var eIcon = ensureItem.Parameters.Add("$icon", SqliteType.Text);
        var eTrash = ensureItem.Parameters.Add("$trash", SqliteType.Integer);
        var eRare = ensureItem.Parameters.Add("$rare", SqliteType.Integer);
        var eUpdated = ensureItem.Parameters.Add("$updated", SqliteType.Text);

        using var englishNameCommand = connection.CreateCommand();
        englishNameCommand.Transaction = transaction;
        englishNameCommand.CommandText = """
            INSERT INTO ItemNames(ItemId, Language, Name)
            VALUES ($id, 'us', $name)
            ON CONFLICT(ItemId, Language) DO UPDATE SET
                Name = CASE
                    WHEN excluded.Name <> '' THEN excluded.Name
                    ELSE ItemNames.Name
                END;
            """;
        var nId = englishNameCommand.Parameters.Add("$id", SqliteType.Integer);
        var nName = englishNameCommand.Parameters.Add("$name", SqliteType.Text);

        using var priceCommand = connection.CreateCommand();
        priceCommand.Transaction = transaction;
        priceCommand.CommandText = """
            INSERT INTO ItemPrices(
                ItemId, Region, UnitPrice, CurrentStock, TotalTrades, UpdatedAtUtc)
            VALUES(
                $id, $region, $price, $stock, $trades, $updated)
            ON CONFLICT(ItemId, Region) DO UPDATE SET
                UnitPrice = excluded.UnitPrice,
                CurrentStock = excluded.CurrentStock,
                TotalTrades = excluded.TotalTrades,
                UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;

        var pId = priceCommand.Parameters.Add("$id", SqliteType.Integer);
        var pRegion = priceCommand.Parameters.Add("$region", SqliteType.Text);
        var pPrice = priceCommand.Parameters.Add("$price", SqliteType.Integer);
        var pStock = priceCommand.Parameters.Add("$stock", SqliteType.Integer);
        var pTrades = priceCommand.Parameters.Add("$trades", SqliteType.Integer);
        var pUpdated = priceCommand.Parameters.Add("$updated", SqliteType.Text);

        string updatedText = fetchedAtUtc.ToString("O");

        // Csak sikeres teljes Garmoth letöltés után jutunk ide, ezért ekkor biztonságos
        // lecserélni az adott régióhoz tartozó korábbi árlistát.
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ItemPrices WHERE Region = $region AND ItemId <> 1;";
            clear.Parameters.AddWithValue("$region", region);
            clear.ExecuteNonQuery();
        }

        foreach (var price in prices)
        {
            eId.Value = (long)price.ItemId;
            eName.Value = price.Name ?? string.Empty;
            eIcon.Value = price.IconUrl ?? string.Empty;
            eTrash.Value = price.IsTrash ? 1 : 0;
            eRare.Value = price.IsRare ? 1 : 0;
            eUpdated.Value = updatedText;
            ensureItem.ExecuteNonQuery();

            if (!string.IsNullOrWhiteSpace(price.Name))
            {
                nId.Value = (long)price.ItemId;
                nName.Value = price.Name;
                englishNameCommand.ExecuteNonQuery();
            }

            pId.Value = (long)price.ItemId;
            pRegion.Value = region;
            pPrice.Value = Math.Max(0, price.BasePrice);
            pStock.Value = Math.Max(0, price.CurrentStock);
            pTrades.Value = Math.Max(0, price.TotalTrades);
            pUpdated.Value = updatedText;
            priceCommand.ExecuteNonQuery();
        }

        // Silver mindig 1 silver.
        using (var silver = connection.CreateCommand())
        {
            silver.Transaction = transaction;
            silver.CommandText = """
                INSERT INTO ItemPrices(ItemId, Region, UnitPrice, UpdatedAtUtc)
                VALUES (1, $region, 1, $updated)
                ON CONFLICT(ItemId, Region) DO UPDATE SET
                    UnitPrice = 1,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            silver.Parameters.AddWithValue("$region", region);
            silver.Parameters.AddWithValue("$updated", updatedText);
            silver.ExecuteNonQuery();
        }

        SetMetadata(connection, transaction, MarketMetadataKey(region), updatedText);
        transaction.Commit();
        _cache.Clear();
    }


    public void UpsertGrindSpots(IReadOnlyCollection<GrindSpotRecord> spots, DateTime fetchedAtUtc)
    {
        if (spots == null || spots.Count == 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var clearDrops = connection.CreateCommand())
        {
            clearDrops.Transaction = transaction;
            clearDrops.CommandText = "DELETE FROM GrindSpotDrops;";
            clearDrops.ExecuteNonQuery();
        }

        using (var clearSpots = connection.CreateCommand())
        {
            clearSpots.Transaction = transaction;
            clearSpots.CommandText = "DELETE FROM GrindSpots;";
            clearSpots.ExecuteNonQuery();
        }

        using var spotCommand = connection.CreateCommand();
        spotCommand.Transaction = transaction;
        spotCommand.CommandText = """
            INSERT INTO GrindSpots(SpotKey, Name, UpdatedAtUtc)
            VALUES ($key, $name, $updated);
            """;
        var sKey = spotCommand.Parameters.Add("$key", SqliteType.Text);
        var sName = spotCommand.Parameters.Add("$name", SqliteType.Text);
        var sUpdated = spotCommand.Parameters.Add("$updated", SqliteType.Text);

        using var dropCommand = connection.CreateCommand();
        dropCommand.Transaction = transaction;
        dropCommand.CommandText = """
            INSERT OR IGNORE INTO GrindSpotDrops(SpotKey, ItemId)
            VALUES ($key, $item);
            """;
        var dKey = dropCommand.Parameters.Add("$key", SqliteType.Text);
        var dItem = dropCommand.Parameters.Add("$item", SqliteType.Integer);

        string updated = fetchedAtUtc.ToString("O");

        foreach (var spot in spots)
        {
            if (string.IsNullOrWhiteSpace(spot.SpotKey) || string.IsNullOrWhiteSpace(spot.Name))
                continue;

            sKey.Value = spot.SpotKey;
            sName.Value = spot.Name;
            sUpdated.Value = updated;
            spotCommand.ExecuteNonQuery();

            foreach (uint itemId in spot.ItemIds.Where(x => x > 0).Distinct())
            {
                dKey.Value = spot.SpotKey;
                dItem.Value = (long)itemId;
                dropCommand.ExecuteNonQuery();
            }
        }

        SetMetadata(connection, transaction, "grind_spots_updated_utc", updated);
        transaction.Commit();
    }

    public IReadOnlyList<SpotCandidate> GetSpotCandidatesForItem(uint itemId)
    {
        var result = new List<SpotCandidate>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT s.SpotKey, s.Name
            FROM GrindSpotDrops d
            INNER JOIN GrindSpots s ON s.SpotKey = d.SpotKey
            WHERE d.ItemId = $item
            ORDER BY s.Name;
            """;
        command.Parameters.AddWithValue("$item", (long)itemId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SpotCandidate
            {
                SpotKey = reader.GetString(0),
                Name = reader.GetString(1)
            });
        }

        return result;
    }

    public HashSet<uint> GetGarmothKnownLootItemIds()
    {
        var result = new HashSet<uint>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT ItemId FROM GrindSpotDrops WHERE ItemId > 0;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long raw = reader.GetInt64(0);
            if (raw > 0 && raw <= uint.MaxValue)
                result.Add((uint)raw);
        }

        return result;
    }

    public void UpdateSessionSpot(long sessionId, string spotKey, string spotName)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sessions
            SET SpotKey = $key, SpotName = $name
            WHERE SessionId = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$key", string.IsNullOrWhiteSpace(spotKey) ? DBNull.Value : spotKey);
        command.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(spotName) ? DBNull.Value : spotName);
        command.ExecuteNonQuery();
    }


    /// <summary>
    /// Resolves a Garmoth numeric grind-spot id using only the local SQLite cache.
    /// This intentionally performs no web request so completed sessions can be
    /// uploaded even when Garmoth's public spot-reference endpoint is blocked.
    /// </summary>
    public int? TryResolveGarmothSpotId(string? spotKey, string? spotName)
    {
        if (int.TryParse(spotKey?.Trim(), out int directId) && directId > 0)
            return directId;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // Prefer a numeric key with the same spot name. The merged local spot
        // cache can contain both an older slug key and the newer numeric Garmoth
        // key for the same zone.
        if (!string.IsNullOrWhiteSpace(spotName))
        {
            using var byName = connection.CreateCommand();
            byName.CommandText = """
                SELECT SpotKey
                FROM GrindSpots
                WHERE LOWER(TRIM(Name)) = LOWER(TRIM($name))
                ORDER BY UpdatedAtUtc DESC;
                """;
            byName.Parameters.AddWithValue("$name", spotName.Trim());

            using var reader = byName.ExecuteReader();
            while (reader.Read())
            {
                string candidate = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (int.TryParse(candidate, out int numericId) && numericId > 0)
                    return numericId;
            }
        }

        // If the old session stored a slug, try matching that local key and then
        // use the row's name to find a numeric sibling.
        if (!string.IsNullOrWhiteSpace(spotKey))
        {
            string? localName = null;
            using (var byKey = connection.CreateCommand())
            {
                byKey.CommandText = "SELECT Name FROM GrindSpots WHERE SpotKey = $key LIMIT 1;";
                byKey.Parameters.AddWithValue("$key", spotKey.Trim());
                object? value = byKey.ExecuteScalar();
                if (value is string text && !string.IsNullOrWhiteSpace(text))
                    localName = text;
            }

            if (!string.IsNullOrWhiteSpace(localName))
            {
                using var sibling = connection.CreateCommand();
                sibling.CommandText = """
                    SELECT SpotKey
                    FROM GrindSpots
                    WHERE LOWER(TRIM(Name)) = LOWER(TRIM($name))
                    ORDER BY UpdatedAtUtc DESC;
                    """;
                sibling.Parameters.AddWithValue("$name", localName.Trim());

                using var reader = sibling.ExecuteReader();
                while (reader.Read())
                {
                    string candidate = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (int.TryParse(candidate, out int numericId) && numericId > 0)
                        return numericId;
                }
            }
        }

        // Last-resort built-in mappings are deliberately tiny and only cover
        // newly released zones that were already verified against Garmoth.
        if (string.Equals(spotName?.Trim(), "Aphrodon Temple", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(spotKey?.Trim(), "aphrodon_temple", StringComparison.OrdinalIgnoreCase))
        {
            return 213;
        }

        return null;
    }


    public IReadOnlyList<uint> GetUnresolvedItemIds(int maxCount = 100)
    {
        maxCount = Math.Clamp(maxCount, 1, 1000);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ItemId
            FROM (
                SELECT i.ItemId AS ItemId
                FROM Items i
                WHERE
                    i.Name = ''
                    OR i.Name LIKE 'Item #%'
                    OR i.Name LIKE 'Unknown Item #%'

                UNION

                SELECT sl.ItemId AS ItemId
                FROM SessionLoot sl
                WHERE
                    sl.ItemName = ''
                    OR sl.ItemName LIKE 'Item #%'
                    OR sl.ItemName LIKE 'Unknown Item #%'
            )
            WHERE ItemId > 1
            ORDER BY ItemId
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", maxCount);

        var result = new List<uint>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(checked((uint)reader.GetInt64(0)));

        return result;
    }

    public void UpsertSupplementalItem(SupplementalItemRecord item, DateTime fetchedAtUtc)
    {
        if (item.ItemId == 0 || string.IsNullOrWhiteSpace(item.Name))
            return;

        string language = NormalizeLanguage(item.Language);
        string updated = fetchedAtUtc.ToString("O");

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var itemCommand = connection.CreateCommand())
        {
            itemCommand.Transaction = transaction;
            itemCommand.CommandText = """
                INSERT INTO Items(ItemId, Name, IconUrl, UpdatedAtUtc)
                VALUES ($id, $name, $icon, $updated)
                ON CONFLICT(ItemId) DO UPDATE SET
                    Name = CASE
                        WHEN excluded.Name <> '' THEN excluded.Name
                        ELSE Items.Name
                    END,
                    IconUrl = CASE
                        WHEN excluded.IconUrl <> '' THEN excluded.IconUrl
                        ELSE Items.IconUrl
                    END,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            itemCommand.Parameters.AddWithValue("$id", (long)item.ItemId);
            itemCommand.Parameters.AddWithValue("$name", item.Name);
            itemCommand.Parameters.AddWithValue("$icon", item.IconUrl ?? string.Empty);
            itemCommand.Parameters.AddWithValue("$updated", updated);
            itemCommand.ExecuteNonQuery();
        }

        using (var nameCommand = connection.CreateCommand())
        {
            nameCommand.Transaction = transaction;
            nameCommand.CommandText = """
                INSERT INTO ItemNames(ItemId, Language, Name)
                VALUES ($id, $language, $name)
                ON CONFLICT(ItemId, Language) DO UPDATE SET
                    Name = excluded.Name;
                """;
            nameCommand.Parameters.AddWithValue("$id", (long)item.ItemId);
            nameCommand.Parameters.AddWithValue("$language", language);
            nameCommand.Parameters.AddWithValue("$name", item.Name);
            nameCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        _cache.Clear();
    }

    public void SetLocalIconPath(uint itemId, string localPath)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Items(ItemId, Name, IconUrl, LocalIconPath, UpdatedAtUtc)
            VALUES ($id, $name, $icon, $path, $updated)
            ON CONFLICT(ItemId) DO UPDATE SET
                LocalIconPath = excluded.LocalIconPath,
                IconUrl = COALESCE(Items.IconUrl, excluded.IconUrl);
            """;
        command.Parameters.AddWithValue("$id", (long)itemId);
        command.Parameters.AddWithValue("$name", $"Item #{itemId}");
        command.Parameters.AddWithValue("$icon", BuildPrimaryIconUrl(itemId));
        command.Parameters.AddWithValue("$path", localPath);
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        _cache.Clear();
    }

    public DatabaseHealth GetHealth(string region, string language)
    {
        region = NormalizeRegion(region);
        language = NormalizeLanguage(language);

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        int itemCount = ExecuteCount(connection, "SELECT COUNT(*) FROM Items;");
        int nameCount = ExecuteCount(connection, "SELECT COUNT(*) FROM ItemNames WHERE Language = $value;", language);
        int marketCount = ExecuteCount(connection, "SELECT COUNT(*) FROM ItemPrices WHERE Region = $value AND ItemId <> 1;", region);
        int iconCount = ExecuteCount(connection, "SELECT COUNT(*) FROM Items WHERE LocalIconPath IS NOT NULL AND LocalIconPath <> ''; ");

        DateTime? catalogUpdated = ParseMetadataDate(GetMetadata(connection, "catalog_updated_utc"));
        DateTime? marketUpdated = ParseMetadataDate(GetMetadata(connection, MarketMetadataKey(region)));

        return new DatabaseHealth
        {
            HasCatalog = catalogUpdated != null,
            HasSelectedLanguage = nameCount > 0,
            CatalogUpdatedUtc = catalogUpdated,
            MarketUpdatedUtc = marketUpdated,
            ItemCount = itemCount,
            NameCount = nameCount,
            MarketPriceCount = marketCount,
            CachedIconCount = iconCount
        };
    }

    private static int ExecuteCount(SqliteConnection connection, string sql, string? value = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (value != null)
            command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static string? GetMetadata(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Metadata WHERE Key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    private static void SetMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Metadata(Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static DateTime? ParseMetadataDate(string? text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return value.ToUniversalTime();

        return null;
    }

    private static string MarketMetadataKey(string region)
        => $"market_{NormalizeRegion(region).ToLowerInvariant()}_updated_utc";

    public static string NormalizeRegion(string region)
        => string.IsNullOrWhiteSpace(region) ? "EU" : region.Trim().ToUpperInvariant();

    public static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "us";

        string value = language.Trim().ToLowerInvariant();
        return value == "en" ? "us" : value;
    }

    public long BeginSession(
        string region,
        string characterName,
        int? classType,
        string className,
        string spec)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        string now = DateTime.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Sessions(
                StartedAtUtc, LastSavedAtUtc, Region, CharacterName, ClassType, ClassName, Spec)
            VALUES (
                $started, $saved, $region, $character, $classType, $className, $spec);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$started", now);
        command.Parameters.AddWithValue("$saved", now);
        command.Parameters.AddWithValue("$region", NormalizeRegion(region));
        command.Parameters.AddWithValue("$character", string.IsNullOrWhiteSpace(characterName) ? DBNull.Value : characterName);
        command.Parameters.AddWithValue("$classType", classType == null ? DBNull.Value : classType.Value);
        command.Parameters.AddWithValue("$className", string.IsNullOrWhiteSpace(className) ? DBNull.Value : className);
        command.Parameters.AddWithValue("$spec", string.IsNullOrWhiteSpace(spec) ? DBNull.Value : spec);

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public void SaveSessionProgress(long sessionId, IReadOnlyCollection<SessionLootSnapshot> loot)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        SaveSessionLootRows(connection, transaction, sessionId, loot);

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE Sessions SET LastSavedAtUtc = $saved WHERE SessionId = $id;";
        update.Parameters.AddWithValue("$saved", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", sessionId);
        update.ExecuteNonQuery();

        transaction.Commit();
    }

    public void EndSession(long sessionId, IReadOnlyCollection<SessionLootSnapshot> loot)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        SaveSessionLootRows(connection, transaction, sessionId, loot);

        string now = DateTime.UtcNow.ToString("O");
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE Sessions SET EndedAtUtc = $ended, LastSavedAtUtc = $saved WHERE SessionId = $id;";
            update.Parameters.AddWithValue("$ended", now);
            update.Parameters.AddWithValue("$saved", now);
            update.Parameters.AddWithValue("$id", sessionId);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void SaveSessionLootRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long sessionId,
        IReadOnlyCollection<SessionLootSnapshot> loot)
    {
        // A snapshot legyen a session aktuális igazsága. Ez azért fontos, mert
        // ha egy itemet futás közben Ignore listára teszünk, a következő autosave
        // a korábban elmentett sorát is eltávolítja az aktív sessionből.
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM SessionLoot WHERE SessionId = $session;";
            clear.Parameters.AddWithValue("$session", sessionId);
            clear.ExecuteNonQuery();
        }

        foreach (var item in loot)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO SessionLoot(
                    SessionId, ItemId, Quantity, ItemName, UnitPrice, IsTrash, IconPath)
                VALUES(
                    $session, $item, $quantity, $name, $price, $trash, $icon)
                ON CONFLICT(SessionId, ItemId)
                DO UPDATE SET
                    Quantity = excluded.Quantity,
                    ItemName = excluded.ItemName,
                    UnitPrice = excluded.UnitPrice,
                    IsTrash = excluded.IsTrash,
                    IconPath = excluded.IconPath;
                """;
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$item", (long)item.ItemId);
            insert.Parameters.AddWithValue("$quantity", checked((long)Math.Min(item.Quantity, (ulong)long.MaxValue)));
            insert.Parameters.AddWithValue("$name", item.Name ?? string.Empty);
            insert.Parameters.AddWithValue("$price", Math.Max(0, item.UnitPrice));
            insert.Parameters.AddWithValue("$trash", item.IsTrash ? 1 : 0);
            insert.Parameters.AddWithValue("$icon", string.IsNullOrWhiteSpace(item.IconPath) ? DBNull.Value : item.IconPath);
            insert.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SessionSummary> GetSessions(string language, bool hideIgnored = true, int limit = 500)
    {
        language = NormalizeLanguage(language);
        var result = new List<SessionSummary>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.SessionId,
                s.StartedAtUtc,
                s.EndedAtUtc,
                s.LastSavedAtUtc,
                s.Region,
                COALESCE(s.CharacterName, ''),
                s.ClassType,
                COALESCE(s.ClassName, ''),
                COALESCE(s.Spec, ''),
                COALESCE(SUM(
                    CASE WHEN $hideIgnored <> 0 AND ig.ItemId IS NOT NULL THEN 0 ELSE
                        CAST(sl.Quantity AS REAL) *
                        CASE
                            WHEN sl.UnitPrice > 0 THEN sl.UnitPrice
                            ELSE COALESCE(NULLIF(p.UnitPrice, 0), i.VendorPrice, 0)
                        END
                    END
                ), 0) AS TotalSilver,
                COALESCE(SUM(
                    CASE
                        WHEN $hideIgnored <> 0 AND ig.ItemId IS NOT NULL THEN 0
                        WHEN sl.IsTrash <> 0 OR COALESCE(i.IsTrash, 0) <> 0 THEN sl.Quantity
                        ELSE 0
                    END
                ), 0) AS TotalTrash,
                COALESCE(s.SpotKey, ''),
                COALESCE(s.SpotName, ''),
                s.GarmothUploadedAtUtc,
                COALESCE(s.GarmothUploadCount, 0),
                s.DropRatePercent
            FROM Sessions s
            LEFT JOIN SessionLoot sl ON sl.SessionId = s.SessionId
            LEFT JOIN Items i ON i.ItemId = sl.ItemId
            LEFT JOIN ItemPrices p ON p.ItemId = sl.ItemId AND p.Region = s.Region
            LEFT JOIN IgnoredItems ig ON ig.ItemId = sl.ItemId
            GROUP BY s.SessionId
            ORDER BY s.StartedAtUtc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        command.Parameters.AddWithValue("$hideIgnored", hideIgnored ? 1 : 0);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTime started = ParseDbDate(reader.GetString(1)) ?? DateTime.UtcNow;
            DateTime? ended = reader.IsDBNull(2) ? null : ParseDbDate(reader.GetString(2));
            DateTime? saved = reader.IsDBNull(3) ? null : ParseDbDate(reader.GetString(3));
            DateTime effectiveEnd = ended ?? saved ?? started;

            decimal totalSilver;
            try
            {
                totalSilver = (decimal)reader.GetDouble(9);
            }
            catch
            {
                totalSilver = 0;
            }

            ulong totalTrash = 0;
            if (!reader.IsDBNull(10))
            {
                long raw = reader.GetInt64(10);
                if (raw > 0)
                    totalTrash = (ulong)raw;
            }

            result.Add(new SessionSummary
            {
                SessionId = reader.GetInt64(0),
                StartedAtUtc = started,
                EffectiveEndUtc = effectiveEnd,
                IsCompleted = ended != null,
                Region = reader.GetString(4),
                CharacterName = reader.GetString(5),
                ClassType = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                ClassName = reader.GetString(7),
                Spec = reader.GetString(8),
                TotalSilver = totalSilver,
                TotalTrash = totalTrash,
                SpotKey = reader.GetString(11),
                SpotName = reader.GetString(12),
                GarmothUploadedAtUtc = reader.IsDBNull(13) ? null : ParseDbDate(reader.GetString(13)),
                GarmothUploadCount = reader.IsDBNull(14) ? 0 : checked((int)reader.GetInt64(14)),
                DropRatePercent = reader.IsDBNull(15) ? null : checked((int)reader.GetInt64(15))
            });
        }

        return result;
    }

    public IReadOnlyList<SessionLootHistoryRow> GetSessionLoot(long sessionId, string language, bool hideIgnored)
    {
        language = NormalizeLanguage(language);
        var result = new List<SessionLootHistoryRow>();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        string region = "EU";
        using (var regionCommand = connection.CreateCommand())
        {
            regionCommand.CommandText = "SELECT Region FROM Sessions WHERE SessionId = $id LIMIT 1;";
            regionCommand.Parameters.AddWithValue("$id", sessionId);
            region = NormalizeRegion(regionCommand.ExecuteScalar()?.ToString() ?? "EU");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                sl.ItemId,
                COALESCE(
                    NULLIF(sl.ItemName, ''),
                    selectedName.Name,
                    englishName.Name,
                    NULLIF(i.Name, ''),
                    'Unknown Item #' || sl.ItemId
                ) AS DisplayName,
                COALESCE(NULLIF(sl.IconPath, ''), i.LocalIconPath, '') AS IconPath,
                sl.Quantity,
                CASE
                    WHEN sl.UnitPrice > 0 THEN sl.UnitPrice
                    ELSE COALESCE(NULLIF(p.UnitPrice, 0), i.VendorPrice, 0)
                END AS UnitPrice,
                CASE
                    WHEN sl.IsTrash <> 0 OR COALESCE(i.IsTrash, 0) <> 0 THEN 1
                    ELSE 0
                END AS IsTrash,
                CASE WHEN ig.ItemId IS NULL THEN 0 ELSE 1 END AS IsIgnored
            FROM SessionLoot sl
            LEFT JOIN Items i ON i.ItemId = sl.ItemId
            LEFT JOIN ItemNames selectedName
              ON selectedName.ItemId = sl.ItemId AND selectedName.Language = $language
            LEFT JOIN ItemNames englishName
              ON englishName.ItemId = sl.ItemId AND englishName.Language = 'us'
            LEFT JOIN ItemPrices p
              ON p.ItemId = sl.ItemId AND p.Region = $region
            LEFT JOIN IgnoredItems ig ON ig.ItemId = sl.ItemId
            WHERE sl.SessionId = $session
              AND ($hideIgnored = 0 OR ig.ItemId IS NULL)
            ORDER BY
                CASE WHEN sl.IsTrash <> 0 OR COALESCE(i.IsTrash, 0) <> 0 THEN 0 ELSE 1 END,
                sl.Quantity DESC,
                DisplayName COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$region", region);
        command.Parameters.AddWithValue("$hideIgnored", hideIgnored ? 1 : 0);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long rawQty = reader.GetInt64(3);
            result.Add(new SessionLootHistoryRow
            {
                ItemId = checked((uint)reader.GetInt64(0)),
                Name = reader.GetString(1),
                IconPath = string.IsNullOrWhiteSpace(reader.GetString(2)) ? null : reader.GetString(2),
                Quantity = rawQty <= 0 ? 0UL : (ulong)rawQty,
                UnitPrice = reader.GetInt64(4),
                IsTrash = reader.GetInt64(5) != 0,
                IsIgnored = reader.GetInt64(6) != 0
            });
        }

        return result;
    }

    public IReadOnlyList<IgnoredItemRecord> GetIgnoredItems()
    {
        var result = new List<IgnoredItemRecord>();
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId, Name, AddedAtUtc FROM IgnoredItems ORDER BY Name COLLATE NOCASE, ItemId;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new IgnoredItemRecord
            {
                ItemId = checked((uint)reader.GetInt64(0)),
                Name = string.IsNullOrWhiteSpace(reader.GetString(1))
                    ? $"Item #{reader.GetInt64(0)}"
                    : reader.GetString(1),
                AddedAtUtc = ParseDbDate(reader.GetString(2)) ?? DateTime.UtcNow
            });
        }

        return result;
    }

    public HashSet<uint> GetIgnoredItemIds()
        => GetIgnoredItems().Select(x => x.ItemId).ToHashSet();

    public bool IsItemIgnored(uint itemId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM IgnoredItems WHERE ItemId = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", (long)itemId);
        return command.ExecuteScalar() != null;
    }

    public void AddIgnoredItem(uint itemId, string name)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO IgnoredItems(ItemId, Name, AddedAtUtc)
            VALUES ($id, $name, $added)
            ON CONFLICT(ItemId) DO UPDATE SET
                Name = CASE WHEN excluded.Name <> '' THEN excluded.Name ELSE IgnoredItems.Name END;
            """;
        command.Parameters.AddWithValue("$id", (long)itemId);
        command.Parameters.AddWithValue("$name", name ?? string.Empty);
        command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RemoveIgnoredItem(uint itemId)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM IgnoredItems WHERE ItemId = $id;";
        command.Parameters.AddWithValue("$id", (long)itemId);
        command.ExecuteNonQuery();
    }

    public void MarkSessionGarmothUploaded(long sessionId, int? dropRatePercent)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Sessions
            SET GarmothUploadedAtUtc = $uploaded,
                GarmothUploadCount = COALESCE(GarmothUploadCount, 0) + 1,
                DropRatePercent = $dropRate
            WHERE SessionId = $id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$uploaded", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$dropRate", dropRatePercent.HasValue ? (object)dropRatePercent.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void UpdateSessionDropRate(long sessionId, int? dropRatePercent)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Sessions SET DropRatePercent = $dropRate WHERE SessionId = $id;";
        command.Parameters.AddWithValue("$id", sessionId);
        command.Parameters.AddWithValue("$dropRate", dropRatePercent.HasValue ? (object)dropRatePercent.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void DeleteSession(long sessionId)
    {
        if (sessionId <= 0)
            return;

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var loot = connection.CreateCommand())
        {
            loot.Transaction = transaction;
            loot.CommandText = "DELETE FROM SessionLoot WHERE SessionId = $id;";
            loot.Parameters.AddWithValue("$id", sessionId);
            loot.ExecuteNonQuery();
        }

        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = "DELETE FROM Sessions WHERE SessionId = $id;";
            session.Parameters.AddWithValue("$id", sessionId);
            session.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static DateTime? ParseDbDate(string? text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            return value.ToUniversalTime();

        return null;
    }
}
