namespace Bezoro.AhkManager.Core.Services;

// Created only by the authenticated backend after pinning the reviewed executables.
// This is never deserialized from a renderer or command-line request.
internal sealed record StartupTransferReview(
    string Entry,
    StartupTaskRegistration? Task,
    string UserSid,
    string CurrentCommand,
    long Revision
);
