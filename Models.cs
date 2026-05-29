namespace DndBot;

public class User
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Role { get; set; } = "player";
    public string? RequestedRole { get; set; }
    public bool IsBanned { get; set; } = false;
    public int Warnings { get; set; } = 0;
    public string? BanReason { get; set; }
}

public class Character
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Race { get; set; } = "";
    public string Class { get; set; } = "";
    public int Level { get; set; } = 1;
}

public class Team
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public long CaptainUserId { get; set; }
    public bool IsPrivate { get; set; } = false;
    public int MaxMembers { get; set; } = 10;
    public DateTime CreatedAt { get; set; }
}

public class TeamMember
{
    public long TeamId { get; set; }
    public long UserId { get; set; }
    public long? CharacterId { get; set; }
    public DateTime JoinedAt { get; set; }
}

public class GameSession
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public string Time { get; set; } = "";
    public long? TeamId { get; set; }
    public long? PlayerId { get; set; }
    public long? CharacterId { get; set; }
    public bool IsConfirmed { get; set; } = false;
    public long? ConfirmedByMasterId { get; set; }
}

public class GameSessionDetails
{
    public long Id { get; set; }
    public DateTime Date { get; set; }
    public string Time { get; set; } = "";
    public long? TeamId { get; set; }
    public long? PlayerId { get; set; }
    public long? CharacterId { get; set; }
    public bool IsConfirmed { get; set; }
    public string? TeamName { get; set; }
    public string? PlayerUsername { get; set; }
    public string? PlayerFirstName { get; set; }
    public string? CharacterName { get; set; }
    public string? CharacterRace { get; set; }
    public string? CharacterClass { get; set; }
    public int? CharacterLevel { get; set; }
}

public class Invitation
{
    public long Id { get; set; }
    public long TeamId { get; set; }
    public long InvitedUserId { get; set; }
    public long InvitedByUserId { get; set; }
    public long? InvitedCharacterId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; }
}

public class Notification
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Type { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; } = false;
}