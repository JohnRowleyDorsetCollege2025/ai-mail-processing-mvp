namespace EnterpriseSearch.Services;

/// <summary>Per-circuit toggle for the design annotation overlays (P1, P2, ...).</summary>
public class AnnotationState
{
    private bool enabled = true;

    public event Action? Changed;

    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            Changed?.Invoke();
        }
    }
}
