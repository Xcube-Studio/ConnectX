using Microsoft.Extensions.Hosting;
using System.CommandLine;
using System.CommandLine.Invocation;
using ConnectX.Client.Interfaces;
using ConnectX.ClientConsole.Helpers;
using ConnectX.Shared.Messages.Group;
using Microsoft.Extensions.Logging;

namespace ConnectX.ClientConsole;

internal static class Commands
{
    public static class Room
    {
        public static readonly Option PasswordOption =
            new Option<string?>(["--password", "-pw"], "The password of the room");

        public static class Create
        {
            public static readonly Option NameOption = new Option<string>(["--name", "-n"], "The name of the room")
                .Required();

            public static readonly Option MaxUserOption =
                new Option<int>(["--max-user", "-mu"], "The max number of players in the room")
                    .Required();

            public static readonly Option DescriptionOption =
                new Option<string?>(["--description", "-d"], "The description of the room");

            public static readonly Option IsPrivateOption = new Option<bool>(["--private", "-p"], "Is the room private")
                .Required()
                .WithDefault(false);
        }

        public static class Join
        {
            public static readonly Option RoomIdOption = new Option<Guid?>(["--room_id", "-id"], "The ID of the room");

            public static readonly Option RoomShortIdOption = new Option<string?>(["--room_short_id", "-sid"], "The short ID of the room");
        }
    }
}

public class ConsoleService(
    Client.Client client,
    IServerLinkHolder serverLinkHolder,
    IZeroTierNodeLinkHolder zeroTierNodeLinkHolder,
    ILogger<ConsoleService> logger)
    : BackgroundService
{
    static string[] ParseArguments(string commandLine)
    {
        var paraChars = commandLine.ToCharArray();
        var inQuote = false;

        for (var index = 0; index < paraChars.Length; index++)
        {
            if (paraChars[index] == '"')
                inQuote = !inQuote;
            if (!inQuote && paraChars[index] == ' ')
                paraChars[index] = '\n';
        }

        return new string(paraChars).Split('\n');
    }

    private Command RoomCommand()
    {
        var room = new Command("room");

        var createCommand = new Command("create", "Create a new room")
        {
            Commands.Room.Create.NameOption,
            Commands.Room.Create.MaxUserOption,
            Commands.Room.Create.DescriptionOption,
            Commands.Room.PasswordOption,
            Commands.Room.Create.IsPrivateOption
        };

        createCommand.SetHandler(HandleRoomCreateAsync);

        var joinCommand = new Command("join", "Join a room")
        {
            Commands.Room.Join.RoomIdOption,
            Commands.Room.Join.RoomShortIdOption,
            Commands.Room.PasswordOption
        };

        joinCommand.SetHandler(HandleRoomJoinAsync);

        room.AddCommand(createCommand);
        room.AddCommand(joinCommand);

        return room;
    }

    private async Task HandleRoomJoinAsync(InvocationContext obj)
    {
        var roomId = (Guid?)obj.ParseResult.GetValueForOption(Commands.Room.Join.RoomIdOption);
        var roomShortId = (string?)obj.ParseResult.GetValueForOption(Commands.Room.Join.RoomShortIdOption);
        var password = (string?)obj.ParseResult.GetValueForOption(Commands.Room.PasswordOption);

        if (!roomId.HasValue && string.IsNullOrEmpty(roomShortId))
        {
            logger.LogError("Room ID or Room Short ID is required");
            return;
        }

        var message = new JoinGroup
        {
            GroupId = roomId ?? Guid.Empty,
            RoomShortId = roomShortId,
            RoomPassword = password,
            UserId = serverLinkHolder.UserId
        };

        var (groupInfo, status, error) = await client.JoinGroupAsync(message, CancellationToken.None);

        logger.LogInformation("Room joined, {@info}, {status:G}, {error}", groupInfo, status, error);
    }

    private async Task HandleRoomCreateAsync(InvocationContext obj)
    {
        var name = (string)obj.ParseResult.GetValueForOption(Commands.Room.Create.NameOption)!;
        var maxUser = (int)obj.ParseResult.GetValueForOption(Commands.Room.Create.MaxUserOption)!;
        var description = (string?)obj.ParseResult.GetValueForOption(Commands.Room.Create.DescriptionOption);
        var password = (string?)obj.ParseResult.GetValueForOption(Commands.Room.PasswordOption);
        var isPrivate = (bool)obj.ParseResult.GetValueForOption(Commands.Room.Create.IsPrivateOption)!;

        var message = new CreateGroup
        {
            IsPrivate = isPrivate,
            RoomName = name,
            RoomDescription = description,
            RoomPassword = password,
            MaxUserCount = maxUser,
            UserId = serverLinkHolder.UserId
        };

        var (groupInfo, status, error) = await client.CreateGroupAsync(message, CancellationToken.None);
        
        logger.LogInformation("Room created, {@info}, {status:G}, {error}", groupInfo, status, error);
    }

    private RootCommand BuildCommand()
    {
        var root = new RootCommand();

        root.AddCommand(RoomCommand());

        return root;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var rootCommand = BuildCommand();

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Yield();

            Console.Write(">:");
            var command = Console.ReadLine();
            await Task.Yield();

            if (string.IsNullOrEmpty(command))
                continue;

            await rootCommand.InvokeAsync(ParseArguments(command));
        }
    }
}