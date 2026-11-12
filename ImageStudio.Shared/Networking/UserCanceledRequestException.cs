using ImageStudio.Localization;

namespace ImageStudio.Networking;

public sealed class UserCanceledRequestException(Exception inner, CancellationToken token)
    : OperationCanceledException(Loc.T("network.request.cancelled"), inner, token);
