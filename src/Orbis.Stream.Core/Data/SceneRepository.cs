using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Data;

/// <summary>
/// The saved canvases: the layouts a live is started from, and the scene each live went on air
/// with, both with the items stacked on them. There is no Java counterpart, the previous version
/// having no concept of a source that is not a file.
/// </summary>
public sealed class SceneRepository
{
    private const string BaseColumns =
        "t.pkid, t.name, t.description, t.width, t.height, t.last_modified, t.is_layout";

    private const string ItemColumns =
        "t.pkid, t.scene_pkid, t.source_kind, t.source_target, t.label, t.x, t.y, t.width, t.height, t.audio_enabled";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SceneRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// Every canvas with its items attached. The item list is what the command builder turns into
    /// a composition, so the order it comes back in is the order the sources are stacked in.
    /// </summary>
    public List<SceneEntity> FindAll() => FindWhere(null);

    /// <summary>The skeletons to start a live from: the scenes of the lives are not among them.</summary>
    public List<SceneEntity> FindLayouts() => FindWhere("t.is_layout = 1");

    /// <summary>Whether a live went on air with this scene: its rows are restarted from it.</summary>
    public bool IsOnAir(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM video v WHERE v.scene_pkid = @pkid);";
        command.Parameters.AddWithValue("@pkid", pkid);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private List<SceneEntity> FindWhere(string? where)
    {
        using var connection = _connectionFactory.Open();
        var scenes = ReadAll(connection, where);

        foreach (var scene in scenes)
        {
            scene.Items = [.. FindItemsOf(connection, scene.Pkid)];
        }

        return scenes;
    }

    public SceneEntity? FindByPkid(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {BaseColumns} FROM stream_scene t WHERE t.pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);

        var scene = ReadScene(command).FirstOrDefault();
        if (scene is not null)
        {
            scene.Items = [.. FindItemsOf(connection, pkid)];
        }

        return scene;
    }

    public IReadOnlyList<SceneItemEntity> FindItemsOf(long scenePkid)
    {
        using var connection = _connectionFactory.Open();
        return FindItemsOf(connection, scenePkid);
    }

    /// <summary>Insert or update, decided by whether the scene has an id yet.</summary>
    public long Save(SceneEntity scene) => scene.Pkid is { } pkid && FindByPkid(pkid) is not null
        ? Updated(pkid, scene)
        : Insert(scene);

    private long Updated(long pkid, SceneEntity scene)
    {
        scene.Pkid = pkid;
        Update(scene);
        return pkid;
    }

    public long Insert(SceneEntity scene)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        var pkid = InsertScene(connection, transaction, scene);
        scene.Pkid = pkid;
        ReplaceItems(connection, transaction, scene);

        transaction.Commit();
        return pkid;
    }

    /// <summary>
    /// Replaces the layout in one go. The items are not diffed: a drag &amp; drop canvas has no
    /// order to preserve between saves, and a delete plus reinsert is the only way to guarantee
    /// that what is stored is what the user is looking at.
    /// </summary>
    public void Update(SceneEntity scene)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE stream_scene
                SET name = @name,
                    description = @description,
                    width = @width,
                    height = @height,
                    last_modified = @lastModified,
                    is_layout = @isLayout
                WHERE pkid = @pkid;
                """;
            command.Parameters.AddWithValue("@name", scene.Name);
            command.Parameters.AddWithValue("@description", SqliteValue.From(scene.Description));
            command.Parameters.AddWithValue("@width", SqliteValue.From(scene.Width));
            command.Parameters.AddWithValue("@height", SqliteValue.From(scene.Height));
            command.Parameters.AddWithValue("@lastModified", SqliteValue.From(scene.LastModified));
            command.Parameters.AddWithValue("@isLayout", scene.IsLayout ? 1 : 0);
            command.Parameters.AddWithValue("@pkid", scene.Pkid);
            command.ExecuteNonQuery();
        }

        DeleteItemsOf(connection, transaction, scene.Pkid);
        ReplaceItems(connection, transaction, scene);
        transaction.Commit();
    }

    /// <summary>The items go with it: the foreign key cascades, but only if the pragma is on.</summary>
    public void Delete(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        DeleteItemsOf(connection, transaction, pkid);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM stream_scene WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    private static long InsertScene(SqliteConnection connection, SqliteTransaction transaction, SceneEntity scene)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO stream_scene (name, description, width, height, last_modified, is_layout)
            VALUES (@name, @description, @width, @height, @lastModified, @isLayout);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@name", scene.Name);
        command.Parameters.AddWithValue("@description", SqliteValue.From(scene.Description));
        command.Parameters.AddWithValue("@width", SqliteValue.From(scene.Width));
        command.Parameters.AddWithValue("@height", SqliteValue.From(scene.Height));
        command.Parameters.AddWithValue("@lastModified", SqliteValue.From(scene.LastModified));
        command.Parameters.AddWithValue("@isLayout", scene.IsLayout ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ReplaceItems(SqliteConnection connection, SqliteTransaction transaction, SceneEntity scene)
    {
        foreach (var item in scene.Items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO stream_scene_item (scene_pkid, source_kind, source_target, label, x, y, width, height, audio_enabled)
                VALUES (@scene, @kind, @target, @label, @x, @y, @width, @height, @audio);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@scene", scene.Pkid);
            command.Parameters.AddWithValue("@kind", (int)item.SourceKind);
            command.Parameters.AddWithValue("@target", item.SourceTarget);
            command.Parameters.AddWithValue("@label", SqliteValue.From(item.Label));
            command.Parameters.AddWithValue("@x", item.X);
            command.Parameters.AddWithValue("@y", item.Y);
            command.Parameters.AddWithValue("@width", item.Width);
            command.Parameters.AddWithValue("@height", item.Height);
            command.Parameters.AddWithValue("@audio", item.AudioEnabled ? 1 : 0);
            item.Pkid = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static void DeleteItemsOf(SqliteConnection connection, SqliteTransaction transaction, long scenePkid)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM stream_scene_item WHERE scene_pkid = @scene;";
        command.Parameters.AddWithValue("@scene", scenePkid);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<SceneItemEntity> FindItemsOf(SqliteConnection connection, long scenePkid)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ItemColumns} FROM stream_scene_item t WHERE t.scene_pkid = @scene ORDER BY t.pkid;";
        command.Parameters.AddWithValue("@scene", scenePkid);

        var items = new List<SceneItemEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(MapItem(reader));
        }

        return items;
    }

    private static List<SceneEntity> ReadAll(SqliteConnection connection, string? where)
    {
        using var command = connection.CreateCommand();
        var filter = where is null ? string.Empty : $" WHERE {where}";
        command.CommandText = $"SELECT {BaseColumns} FROM stream_scene t{filter} ORDER BY t.pkid;";
        return ReadScene(command);
    }

    private static List<SceneEntity> ReadScene(SqliteCommand command)
    {
        var scenes = new List<SceneEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            scenes.Add(new SceneEntity
            {
                Pkid = reader.GetInt64(0),
                Name = reader.GetString(1),
                Description = SqliteValue.ToText(reader.GetValue(2)),
                Width = SqliteValue.ToNullableInt32(reader.GetValue(3)),
                Height = SqliteValue.ToNullableInt32(reader.GetValue(4)),
                LastModified = SqliteValue.ToNullableDateTime(reader.GetValue(5)),
                IsLayout = SqliteValue.ToBoolean(reader.GetValue(6))
            });
        }

        return scenes;
    }

    private static SceneItemEntity MapItem(SqliteDataReader reader) => new()
    {
        Pkid = reader.GetInt64(0),
        ScenePkid = reader.GetInt64(1),
        SourceKind = SourceKindExtensions.TryParse(SqliteValue.ToText(reader.GetValue(2)), out var kind)
            ? kind
            : SourceKind.File,
        SourceTarget = reader.GetString(3),
        Label = SqliteValue.ToText(reader.GetValue(4)),
        X = (int)SqliteValue.ToInt64(reader.GetValue(5)),
        Y = (int)SqliteValue.ToInt64(reader.GetValue(6)),
        Width = (int)SqliteValue.ToInt64(reader.GetValue(7)),
        Height = (int)SqliteValue.ToInt64(reader.GetValue(8)),
        AudioEnabled = SqliteValue.ToBoolean(reader.GetValue(9))
    };
}
