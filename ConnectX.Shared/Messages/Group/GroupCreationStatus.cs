namespace ConnectX.Shared.Messages.Group;

public enum GroupCreationStatus
{
    Succeeded = 0,
    GroupNotExists = 1,
    UserNotExists = 2,
    SessionDetached = 3,
    AlreadyInRoom = 4,
    GroupIsFull = 5,
    PasswordIncorrect = 6,

    NetworkControllerNotReady = 7,
    NetworkControllerError = 8,
    InternalError = 9,

    Other = 10,

    // This is used to indicate that the client should redirect to another server
    NeedRedirect = 11
}