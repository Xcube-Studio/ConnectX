namespace ConnectX.Shared.Messages.Group;

public enum GroupCreationStatus
{
    Succeeded,
    GroupNotExists,
    UserNotExists,
    SessionDetached,
    AlreadyInRoom,
    GroupIsFull,
    PasswordIncorrect,

    NetworkControllerNotReady,
    NetworkControllerError,
    InternalError,

    Other
}