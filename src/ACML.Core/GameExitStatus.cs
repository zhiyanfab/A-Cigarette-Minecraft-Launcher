namespace ACML.Core;
public enum GameExitStatus { Stopped, Exited, Failed }
public static class GameExitClassifier
{
    public static GameExitStatus Classify(bool stopRequested,bool startupCompleted,int exitCode)
        => stopRequested ? GameExitStatus.Stopped : startupCompleted && exitCode == 0 ? GameExitStatus.Exited : GameExitStatus.Failed;
}
