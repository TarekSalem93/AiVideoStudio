using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiVideoStudio.Services.Models;

public class Scene : INotifyPropertyChanged
{
    private int _sceneNumber;
    private string _narration = string.Empty;
    private string _visualPrompt = string.Empty;
    private string _negativePrompt = string.Empty;
    private double _estimatedDuration;
    private bool _isGenerated;

    public int SceneNumber
    {
        get => _sceneNumber;
        set => SetProperty(ref _sceneNumber, value);
    }

    public string Narration
    {
        get => _narration;
        set => SetProperty(ref _narration, value);
    }

    public string VisualPrompt
    {
        get => _visualPrompt;
        set => SetProperty(ref _visualPrompt, value);
    }

    public string NegativePrompt
    {
        get => _negativePrompt;
        set => SetProperty(ref _negativePrompt, value);
    }

    public double EstimatedDuration
    {
        get => _estimatedDuration;
        set => SetProperty(ref _estimatedDuration, value);
    }

    public bool IsGenerated
    {
        get => _isGenerated;
        set => SetProperty(ref _isGenerated, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(backingStore, value))
            return false;

        backingStore = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}