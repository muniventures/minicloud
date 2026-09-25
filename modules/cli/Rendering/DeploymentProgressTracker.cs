namespace Minicloud.Cli.Rendering;

public sealed class DeploymentProgressTracker
{
    private readonly DeploymentPlan _plan;
    private readonly int _includedStageCount;
    private int _completedSecrets;
    private int _completedArtifactOps;
    private int _deployOpCompleted; // 0, 1 (created), 2 (terminal)
    private int _highestPercentage;
    private bool _failed;

    public DeploymentProgressTracker(DeploymentPlan plan)
    {
        _plan = plan;
        var count = 0;
        if (plan.HasSecretsStage && plan.TotalSecrets > 0)
        {
            count++;
        }
        if (plan.HasArtifactsStage && plan.SourceServices.Count > 0)
        {
            count++;
        }
        if (plan.HasDeployStage)
        {
            count++;
        }
        _includedStageCount = count;
    }

    public void OnSecretCompleted()
    {
        if (_failed) return;
        _completedSecrets++;
        UpdatePercentage();
    }

    public void OnArtifactOpCompleted()
    {
        if (_failed) return;
        _completedArtifactOps++;
        UpdatePercentage();
    }

    public void OnDeploymentCreated()
    {
        if (_failed) return;
        _deployOpCompleted = 1;
        UpdatePercentage();
    }

    public void OnDeploymentSucceeded()
    {
        _deployOpCompleted = 2;
        _highestPercentage = 100;
    }

    public void OnDeploymentFailed()
    {
        _failed = true;
        // Do not advance progress on failure; freeze at the percentage reached.
        // Guarantee failure never displays 100%.
        if (_highestPercentage >= 100)
        {
            _highestPercentage = 99;
        }
    }

    public int CurrentPercentage => _highestPercentage;

    private void UpdatePercentage()
    {
        if (_failed || _includedStageCount == 0)
        {
            return;
        }

        var stageWeight = 1.0 / _includedStageCount;
        var totalFraction = 0.0;

        if (_plan.HasSecretsStage && _plan.TotalSecrets > 0)
        {
            var secretsFraction = Math.Clamp((double)_completedSecrets / _plan.TotalSecrets, 0.0, 1.0);
            totalFraction += secretsFraction * stageWeight;
        }

        if (_plan.HasArtifactsStage && _plan.SourceServices.Count > 0)
        {
            var totalArtifactOps = _plan.SourceServices.Count * 2;
            var artifactsFraction = Math.Clamp((double)_completedArtifactOps / totalArtifactOps, 0.0, 1.0);
            totalFraction += artifactsFraction * stageWeight;
        }

        if (_plan.HasDeployStage)
        {
            var deployFraction = Math.Clamp((double)_deployOpCompleted / 2.0, 0.0, 1.0);
            totalFraction += deployFraction * stageWeight;
        }

        var calculated = (int)Math.Floor(totalFraction * 100.0);
        // During in-flight operations, clamp to at most 99 until explicit success
        calculated = Math.Clamp(calculated, 0, 99);
        _highestPercentage = Math.Max(_highestPercentage, calculated);
    }
}
