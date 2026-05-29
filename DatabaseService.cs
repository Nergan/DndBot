using Microsoft.Data.Sqlite;
using Dapper;
using System.Data;

namespace DndBot;

public class DatabaseService : IDisposable
{
    private static IDbConnection _connection;
    private static readonly object _lock = new();
    private static int _instanceCount = 0;
    private readonly int _instanceId;

    public DatabaseService()
    {
        lock (_lock)
        {
            _instanceId = ++_instanceCount;
            Console.WriteLine($"[DB] Создан экземпляр DatabaseService #{_instanceId}");

            if (_connection == null)
            {
                var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "dndbot.db");
                Console.WriteLine($"[DB] Создание статического подключения к файлу: {dbPath}");
                _connection = new SqliteConnection($"Data Source={dbPath}");
                _connection.Open();
                _connection.Execute("PRAGMA journal_mode=WAL;");
                _connection.Execute("PRAGMA synchronous=NORMAL;");
                _connection.Execute("PRAGMA foreign_keys=ON;");
                InitializeDatabase();
                Console.WriteLine($"[DB] Инициализация завершена. Файл базы: {dbPath}");
            }
            else
            {
                Console.WriteLine($"[DB] Экземпляр #{_instanceId} использует существующее подключение (хеш: {_connection.GetHashCode()})");
            }
        }
    }

    private void InitializeDatabase()
    {
        _connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY,
                Username TEXT,
                FirstName TEXT,
                LastName TEXT,
                Role TEXT NOT NULL DEFAULT 'player',
                RequestedRole TEXT,
                IsBanned INTEGER NOT NULL DEFAULT 0,
                Warnings INTEGER NOT NULL DEFAULT 0,
                BanReason TEXT
            );

            CREATE TABLE IF NOT EXISTS Characters (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Name TEXT NOT NULL,
                Race TEXT NOT NULL,
                Class TEXT NOT NULL,
                Level INTEGER NOT NULL,
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Teams (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                CaptainUserId INTEGER NOT NULL,
                IsPrivate INTEGER NOT NULL DEFAULT 0,
                MaxMembers INTEGER NOT NULL DEFAULT 10,
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY(CaptainUserId) REFERENCES Users(Id)
            );

            CREATE TABLE IF NOT EXISTS TeamMembers (
                TeamId INTEGER NOT NULL,
                UserId INTEGER NOT NULL,
                CharacterId INTEGER,
                JoinedAt TEXT NOT NULL,
                PRIMARY KEY(TeamId, UserId),
                FOREIGN KEY(TeamId) REFERENCES Teams(Id) ON DELETE CASCADE,
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                FOREIGN KEY(CharacterId) REFERENCES Characters(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS GameSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Date TEXT NOT NULL,
                Time TEXT NOT NULL,
                TeamId INTEGER,
                PlayerId INTEGER,
                CharacterId INTEGER,
                IsConfirmed INTEGER NOT NULL DEFAULT 0,
                ConfirmedByMasterId INTEGER,
                FOREIGN KEY(TeamId) REFERENCES Teams(Id) ON DELETE CASCADE,
                FOREIGN KEY(PlayerId) REFERENCES Users(Id) ON DELETE CASCADE,
                FOREIGN KEY(CharacterId) REFERENCES Characters(Id) ON DELETE CASCADE,
                FOREIGN KEY(ConfirmedByMasterId) REFERENCES Users(Id)
            );

            CREATE TABLE IF NOT EXISTS Invitations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TeamId INTEGER NOT NULL,
                InvitedUserId INTEGER NOT NULL,
                InvitedByUserId INTEGER NOT NULL,
                InvitedCharacterId INTEGER,
                Status TEXT NOT NULL DEFAULT 'pending',
                CreatedAt TEXT NOT NULL,
                FOREIGN KEY(TeamId) REFERENCES Teams(Id) ON DELETE CASCADE,
                FOREIGN KEY(InvitedUserId) REFERENCES Users(Id),
                FOREIGN KEY(InvitedByUserId) REFERENCES Users(Id),
                FOREIGN KEY(InvitedCharacterId) REFERENCES Characters(Id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS Notifications (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Type TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                IsRead INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE
            );
        ");

        _connection.Execute("CREATE INDEX IF NOT EXISTS idx_gamesessions_date ON GameSessions(Date);");
        _connection.Execute("CREATE INDEX IF NOT EXISTS idx_teammembers_userid ON TeamMembers(UserId);");
        _connection.Execute("CREATE INDEX IF NOT EXISTS idx_teammembers_teamid ON TeamMembers(TeamId);");
        _connection.Execute("CREATE INDEX IF NOT EXISTS idx_invitations_user_status ON Invitations(InvitedUserId, Status);");
        _connection.Execute("CREATE INDEX IF NOT EXISTS idx_notifications_user_read ON Notifications(UserId, IsRead);");
    }

    // ====== Users ======
    public Task<User?> GetUser(long telegramId)
    {
        return _connection.QueryFirstOrDefaultAsync<User>(
            "SELECT Id, Username, FirstName, LastName, Role, RequestedRole, IsBanned, Warnings, BanReason FROM Users WHERE Id = @Id",
            new { Id = telegramId });
    }

    public Task AddOrUpdateUser(User user)
    {
        return _connection.ExecuteAsync(@"
            INSERT OR REPLACE INTO Users (Id, Username, FirstName, LastName, Role, RequestedRole, IsBanned, Warnings, BanReason)
            VALUES (@Id, @Username, @FirstName, @LastName, @Role, @RequestedRole, @IsBanned, @Warnings, @BanReason)",
            user);
    }

    public Task UpdateUserRole(long userId, string role)
    {
        return _connection.ExecuteAsync("UPDATE Users SET Role = @role WHERE Id = @userId", new { userId, role });
    }

    public Task UpdateUserRequestedRole(long userId, string? requestedRole)
    {
        return _connection.ExecuteAsync("UPDATE Users SET RequestedRole = @requestedRole WHERE Id = @userId", new { userId, requestedRole });
    }

    public async Task<List<User>> GetUsersWithRequestedRole(string role)
    {
        return (await _connection.QueryAsync<User>("SELECT * FROM Users WHERE RequestedRole = @role", new { role })).ToList();
    }

    public async Task<List<User>> GetAllUsersExceptAdmin(long adminId)
    {
        return (await _connection.QueryAsync<User>(
            "SELECT Id, Username, FirstName, LastName, Role, IsBanned, Warnings FROM Users WHERE Id != @adminId",
            new { adminId })).ToList();
    }

    public async Task AddWarning(long userId, string reason)
    {
        var user = await GetUser(userId);
        if (user == null) return;
        var newWarnings = user.Warnings + 1;
        await _connection.ExecuteAsync("UPDATE Users SET Warnings = @warnings WHERE Id = @userId", new { warnings = newWarnings, userId });
        if (newWarnings >= 3)
        {
            await BanUser(userId, $"Автоматический бан за 3 предупреждения. Последнее: {reason}");
        }
    }

    public Task BanUser(long userId, string reason)
    {
        return _connection.ExecuteAsync("UPDATE Users SET IsBanned = 1, BanReason = @reason WHERE Id = @userId", new { reason, userId });
    }

    public Task UnbanUser(long userId)
    {
        return _connection.ExecuteAsync("UPDATE Users SET IsBanned = 0, BanReason = NULL, Warnings = 0 WHERE Id = @userId", new { userId });
    }

    // ====== Characters ======
    public async Task<List<Character>> GetUserCharacters(long userId)
    {
        var chars = (await _connection.QueryAsync<Character>("SELECT * FROM Characters WHERE UserId = @userId", new { userId })).ToList();
        Console.WriteLine($"[DB] GetUserCharacters for {userId}: found {chars.Count}");
        return chars;
    }

    public async Task<List<Character>> GetAllCharacters()
    {
        return (await _connection.QueryAsync<Character>("SELECT * FROM Characters")).ToList();
    }

    public Task<Character?> GetCharacter(long characterId)
    {
        return _connection.QueryFirstOrDefaultAsync<Character>("SELECT * FROM Characters WHERE Id = @id", new { id = characterId });
    }

    public async Task<long> CreateCharacter(Character character)
    {
        Console.WriteLine($"Добавление персонажа: UserId={character.UserId}, Name={character.Name}");
        var newId = await _connection.ExecuteScalarAsync<long>(
            @"INSERT INTO Characters (UserId, Name, Race, Class, Level)
              VALUES (@UserId, @Name, @Race, @Class, @Level);
              SELECT last_insert_rowid();",
            character);
        character.Id = newId;
        Console.WriteLine($"Персонаж добавлен с ID={newId}");
        return newId;
    }

    public Task UpdateCharacter(Character character)
    {
        return _connection.ExecuteAsync("UPDATE Characters SET Name = @Name, Race = @Race, Class = @Class, Level = @Level WHERE Id = @Id", character);
    }

    public Task DeleteCharacter(long characterId)
    {
        Console.WriteLine($"[DB] Удаление персонажа {characterId}");
        return _connection.ExecuteAsync("DELETE FROM Characters WHERE Id = @id", new { id = characterId });
    }

    public Task<bool> IsCharacterCaptainInAnyTeam(long characterId)
    {
        return _connection.ExecuteScalarAsync<bool>(
            @"SELECT EXISTS(
                SELECT 1 FROM Teams t
                INNER JOIN TeamMembers tm ON tm.TeamId = t.Id AND tm.UserId = t.CaptainUserId
                WHERE tm.CharacterId = @characterId)", new { characterId });
    }

    // ====== Teams ======
    public Task<Team?> GetTeam(long teamId)
    {
        return _connection.QueryFirstOrDefaultAsync<Team>("SELECT * FROM Teams WHERE Id = @teamId", new { teamId });
    }

    public async Task<List<Team>> GetUserTeams(long userId)
    {
        return (await _connection.QueryAsync<Team>(
            "SELECT t.* FROM Teams t INNER JOIN TeamMembers tm ON tm.TeamId = t.Id WHERE tm.UserId = @userId", new { userId })).ToList();
    }

    public async Task<List<Team>> GetOpenTeams()
    {
        return (await _connection.QueryAsync<Team>("SELECT * FROM Teams WHERE IsPrivate = 0")).ToList();
    }

    public async Task<long> CreateTeam(Team team)
    {
        var captainExists = await _connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM Users WHERE Id = @captainId)", new { captainId = team.CaptainUserId });
        if (!captainExists) throw new Exception("Captain user not found");

        using var tx = _connection.BeginTransaction();
        var teamId = await _connection.QuerySingleAsync<long>(
            @"INSERT INTO Teams (Name, CaptainUserId, IsPrivate, MaxMembers, CreatedAt)
              VALUES (@Name, @CaptainUserId, @IsPrivate, @MaxMembers, @CreatedAt);
              SELECT last_insert_rowid();",
            new { team.Name, team.CaptainUserId, team.IsPrivate, team.MaxMembers, CreatedAt = DateTime.UtcNow.ToString("o") },
            transaction: tx);
        await _connection.ExecuteAsync(
            "INSERT INTO TeamMembers (TeamId, UserId, CharacterId, JoinedAt) VALUES (@teamId, @captainId, NULL, @joinedAt)",
            new { teamId, captainId = team.CaptainUserId, joinedAt = DateTime.UtcNow.ToString("o") },
            transaction: tx);
        tx.Commit();
        return teamId;
    }

    public Task DeleteTeam(long teamId)
    {
        return _connection.ExecuteAsync("DELETE FROM Teams WHERE Id = @teamId", new { teamId });
    }

    public async Task<bool> IsUserInTeam(long userId, long teamId)
    {
        return await _connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM TeamMembers WHERE TeamId = @teamId AND UserId = @userId",
            new { teamId, userId }) > 0;
    }

    public async Task AddTeamMember(long teamId, long userId, long? characterId = null)
    {
        var teamExists = await _connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM Teams WHERE Id = @teamId)", new { teamId });
        if (!teamExists) return;

        var userExists = await _connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM Users WHERE Id = @userId)", new { userId });
        if (!userExists) return;

        if (characterId.HasValue)
        {
            var validChar = await _connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM Characters WHERE Id = @charId AND UserId = @userId)",
                new { charId = characterId.Value, userId });
            if (!validChar) characterId = null;
        }

        await _connection.ExecuteAsync(
            "INSERT OR IGNORE INTO TeamMembers (TeamId, UserId, CharacterId, JoinedAt) VALUES (@teamId, @userId, @characterId, @joinedAt)",
            new { teamId, userId, characterId, joinedAt = DateTime.UtcNow.ToString("o") });
    }

    public Task RemoveTeamMember(long teamId, long userId)
    {
        return _connection.ExecuteAsync("DELETE FROM TeamMembers WHERE TeamId = @teamId AND UserId = @userId", new { teamId, userId });
    }

    public Task UpdateTeamMemberCharacter(long teamId, long userId, long? characterId)
    {
        return _connection.ExecuteAsync("UPDATE TeamMembers SET CharacterId = @characterId WHERE TeamId = @teamId AND UserId = @userId",
            new { teamId, userId, characterId });
    }

    public async Task<List<(User user, Character? character)>> GetTeamMembersWithCharacters(long teamId)
    {
        var rows = await _connection.QueryAsync(
            @"SELECT u.Id, u.Username, u.FirstName, c.Id as CharacterId, c.Name, c.Race, c.Class, c.Level
              FROM TeamMembers tm
              JOIN Users u ON u.Id = tm.UserId
              LEFT JOIN Characters c ON c.Id = tm.CharacterId
              WHERE tm.TeamId = @teamId", new { teamId });

        var result = new List<(User, Character?)>();
        foreach (var row in rows)
        {
            var user = new User { Id = row.Id, Username = row.Username, FirstName = row.FirstName };
            Character? chr = null;
            if (row.CharacterId != null)
            {
                chr = new Character
                {
                    Id = row.CharacterId,
                    Name = row.Name,
                    Race = row.Race,
                    Class = row.Class,
                    Level = (int)row.Level
                };
            }
            result.Add((user, chr));
        }
        return result;
    }

    public async Task<List<(User user, List<Character> characters)>> GetUsersWithCharactersExceptTeam(long teamId, long exceptUserId)
    {
        var rows = await _connection.QueryAsync(
            @"SELECT u.Id, u.Username, u.FirstName, c.Id as CharacterId, c.Name, c.Race, c.Class, c.Level
              FROM Users u
              JOIN Characters c ON c.UserId = u.Id
              WHERE u.Id != @exceptUserId
              AND NOT EXISTS (SELECT 1 FROM TeamMembers tm WHERE tm.TeamId = @teamId AND tm.UserId = u.Id)
              ORDER BY u.Username",
            new { teamId, exceptUserId });

        var dict = new Dictionary<long, (User, List<Character>)>();
        foreach (var row in rows)
        {
            if (!dict.ContainsKey(row.Id))
                dict[row.Id] = (new User { Id = row.Id, Username = row.Username, FirstName = row.FirstName }, new List<Character>());
            dict[row.Id].Item2.Add(new Character
            {
                Id = row.CharacterId,
                Name = row.Name,
                Race = row.Race,
                Class = row.Class,
                Level = (int)row.Level
            });
        }
        return dict.Values.ToList();
    }

    // ====== Invitations ======
    public async Task<long> CreateInvitation(long teamId, long invitedUserId, long invitedByUserId, long? invitedCharacterId = null)
    {
        return await _connection.QuerySingleAsync<long>(
            @"INSERT INTO Invitations (TeamId, InvitedUserId, InvitedByUserId, InvitedCharacterId, Status, CreatedAt)
              VALUES (@teamId, @invitedUserId, @invitedByUserId, @invitedCharacterId, 'pending', @createdAt);
              SELECT last_insert_rowid();",
            new { teamId, invitedUserId, invitedByUserId, invitedCharacterId, createdAt = DateTime.UtcNow.ToString("o") });
    }

    public Task<Invitation?> GetInvitationById(long id)
    {
        return _connection.QueryFirstOrDefaultAsync<Invitation>("SELECT * FROM Invitations WHERE Id = @id", new { id });
    }

    public async Task<List<Invitation>> GetPendingInvitationsForUser(long userId)
    {
        return (await _connection.QueryAsync<Invitation>(
            "SELECT * FROM Invitations WHERE InvitedUserId = @userId AND Status = 'pending'", new { userId })).ToList();
    }

    public Task UpdateInvitationStatus(long id, string status)
    {
        return _connection.ExecuteAsync("UPDATE Invitations SET Status = @status WHERE Id = @id", new { status, id });
    }

    // ====== Game Sessions ======
    public Task AddGameSession(GameSession session)
    {
        return _connection.ExecuteAsync(
            @"INSERT INTO GameSessions (Date, Time, TeamId, PlayerId, CharacterId, IsConfirmed, ConfirmedByMasterId)
              VALUES (@Date, @Time, @TeamId, @PlayerId, @CharacterId, @IsConfirmed, @ConfirmedByMasterId)",
            new
            {
                Date = session.Date.ToString("yyyy-MM-dd"),
                Time = session.Time,
                TeamId = session.TeamId,
                PlayerId = session.PlayerId,
                CharacterId = session.CharacterId,
                IsConfirmed = session.IsConfirmed ? 1 : 0,
                ConfirmedByMasterId = session.ConfirmedByMasterId
            });
    }

    public async Task<List<GameSession>> GetGameSessionsForDate(DateTime date)
    {
        return (await _connection.QueryAsync<GameSession>(
            "SELECT * FROM GameSessions WHERE Date = @date", new { date = date.ToString("yyyy-MM-dd") })).ToList();
    }

    public async Task<List<GameSession>> GetGameSessionsForDateRange(DateTime start, DateTime end)
    {
        return (await _connection.QueryAsync<GameSession>(
            "SELECT * FROM GameSessions WHERE Date BETWEEN @start AND @end",
            new { start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") })).ToList();
    }

    public async Task<List<GameSessionDetails>> GetGameSessionsWithDetails(DateTime date)
    {
        var sql = @"
            SELECT s.Id, s.Date, s.Time, s.TeamId, s.PlayerId, s.CharacterId, s.IsConfirmed,
                   t.Name AS TeamName,
                   u.Username AS PlayerUsername, u.FirstName AS PlayerFirstName,
                   ch.Name AS CharacterName, ch.Race AS CharacterRace, ch.Class AS CharacterClass, ch.Level AS CharacterLevel
            FROM GameSessions s
            LEFT JOIN Teams t ON s.TeamId = t.Id
            LEFT JOIN Users u ON s.PlayerId = u.Id
            LEFT JOIN Characters ch ON s.CharacterId = ch.Id
            WHERE s.Date = @date";
        return (await _connection.QueryAsync<GameSessionDetails>(sql, new { date = date.ToString("yyyy-MM-dd") })).ToList();
    }

    public async Task<List<GameSessionDetails>> GetGameSessionsWithDetailsRange(DateTime start, DateTime end)
    {
        var sql = @"
            SELECT s.Id, s.Date, s.Time, s.TeamId, s.PlayerId, s.CharacterId, s.IsConfirmed,
                   t.Name AS TeamName,
                   u.Username AS PlayerUsername, u.FirstName AS PlayerFirstName,
                   ch.Name AS CharacterName, ch.Race AS CharacterRace, ch.Class AS CharacterClass, ch.Level AS CharacterLevel
            FROM GameSessions s
            LEFT JOIN Teams t ON s.TeamId = t.Id
            LEFT JOIN Users u ON s.PlayerId = u.Id
            LEFT JOIN Characters ch ON s.CharacterId = ch.Id
            WHERE s.Date BETWEEN @start AND @end
            ORDER BY s.Date, s.Time";
        return (await _connection.QueryAsync<GameSessionDetails>(sql,
            new { start = start.ToString("yyyy-MM-dd"), end = end.ToString("yyyy-MM-dd") })).ToList();
    }

    public async Task<List<GameSession>> GetConfirmedGameSessionsForDate(DateTime date)
    {
        return (await _connection.QueryAsync<GameSession>(
            "SELECT * FROM GameSessions WHERE Date = @date AND IsConfirmed = 1",
            new { date = date.ToString("yyyy-MM-dd") })).ToList();
    }

    public Task ConfirmGameSession(long sessionId, long masterId)
    {
        return _connection.ExecuteAsync(
            "UPDATE GameSessions SET IsConfirmed = 1, ConfirmedByMasterId = @masterId WHERE Id = @id",
            new { masterId, id = sessionId });
    }

    // ====== Notifications ======
    public Task AddNotification(Notification notification)
    {
        return _connection.ExecuteAsync(
            @"INSERT INTO Notifications (UserId, Type, Content, CreatedAt, IsRead)
              VALUES (@UserId, @Type, @Content, @CreatedAt, @IsRead)",
            new { notification.UserId, notification.Type, notification.Content, CreatedAt = DateTime.UtcNow.ToString("o"), IsRead = false });
    }

    public async Task<List<Notification>> GetUnreadNotifications(long userId)
    {
        return (await _connection.QueryAsync<Notification>(
            "SELECT * FROM Notifications WHERE UserId = @userId AND IsRead = 0 ORDER BY CreatedAt DESC", new { userId })).ToList();
    }

    public async Task<List<Notification>> GetAllNotifications(long userId)
    {
        return (await _connection.QueryAsync<Notification>(
            "SELECT * FROM Notifications WHERE UserId = @userId ORDER BY CreatedAt DESC", new { userId })).ToList();
    }

    public Task<Notification?> GetNotificationById(long id)
    {
        return _connection.QueryFirstOrDefaultAsync<Notification>("SELECT * FROM Notifications WHERE Id = @id", new { id });
    }

    public Task MarkNotificationAsRead(long notificationId)
    {
        return _connection.ExecuteAsync("UPDATE Notifications SET IsRead = 1 WHERE Id = @id", new { id = notificationId });
    }

    public void Dispose()
    {
        Console.WriteLine($"[DB] Dispose экземпляра #{_instanceId} (соединение остаётся открытым)");
    }
}