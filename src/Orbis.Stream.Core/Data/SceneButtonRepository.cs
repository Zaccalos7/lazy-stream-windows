using System.Globalization;
using Microsoft.Data.Sqlite;
using Orbis.Stream.Core.Domain;

namespace Orbis.Stream.Core.Data;

/// <summary>The buttons of the scene deck (see <see cref="SceneButtonEntity"/>), in the order the deck shows them.</summary>
public sealed class SceneButtonRepository
{
    private const string Columns = "t.pkid, t.position, t.label, t.media_name, t.hotkey, t.last_modified";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SceneButtonRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public List<SceneButtonEntity> FindAll()
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scene_button t ORDER BY t.position, t.pkid;";
        return Read(command);
    }

    public SceneButtonEntity? FindByPkid(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM scene_button t WHERE t.pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        return Read(command).FirstOrDefault();
    }

    public int Count()
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM scene_button;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a button still carries this file: the file is what it puts on air, so it stays while one does.</summary>
    public bool UsesMedia(string mediaName)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM scene_button t WHERE t.media_name = @name);";
        command.Parameters.AddWithValue("@name", mediaName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>A new button goes after the last one, which is where the deck draws the one just added.</summary>
    public long Insert(SceneButtonEntity button)
    {
        ArgumentNullException.ThrowIfNull(button);

        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO scene_button (position, label, media_name, hotkey, last_modified)
            VALUES ((SELECT coalesce(max(position), 0) + 1 FROM scene_button), @label, @media, @hotkey, @lastModified);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@label", button.Label);
        command.Parameters.AddWithValue("@media", button.MediaName);
        command.Parameters.AddWithValue("@hotkey", SqliteValue.From(button.Hotkey));
        command.Parameters.AddWithValue("@lastModified", SqliteValue.From(button.LastModified));
        button.Pkid = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return button.Pkid;
    }

    public void Update(SceneButtonEntity button)
    {
        ArgumentNullException.ThrowIfNull(button);

        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE scene_button
            SET label = @label, media_name = @media, hotkey = @hotkey, last_modified = @lastModified
            WHERE pkid = @pkid;
            """;
        command.Parameters.AddWithValue("@label", button.Label);
        command.Parameters.AddWithValue("@media", button.MediaName);
        command.Parameters.AddWithValue("@hotkey", SqliteValue.From(button.Hotkey));
        command.Parameters.AddWithValue("@lastModified", SqliteValue.From(button.LastModified));
        command.Parameters.AddWithValue("@pkid", button.Pkid);
        command.ExecuteNonQuery();
    }

    public void Delete(long pkid)
    {
        using var connection = _connectionFactory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM scene_button WHERE pkid = @pkid;";
        command.Parameters.AddWithValue("@pkid", pkid);
        command.ExecuteNonQuery();
    }

    private static List<SceneButtonEntity> Read(SqliteCommand command)
    {
        var buttons = new List<SceneButtonEntity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            buttons.Add(new SceneButtonEntity
            {
                Pkid = reader.GetInt64(0),
                Position = (int)SqliteValue.ToInt64(reader.GetValue(1)),
                Label = reader.GetString(2),
                MediaName = reader.GetString(3),
                Hotkey = SqliteValue.ToText(reader.GetValue(4)),
                LastModified = SqliteValue.ToNullableDateTime(reader.GetValue(5))
            });
        }

        return buttons;
    }
}
