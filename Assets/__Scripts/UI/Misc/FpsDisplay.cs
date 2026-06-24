using UnityEngine;
using TMPro;

public class FpsDisplay : MonoBehaviour
{
    public const float FramerateSampleTime = 0.5f;

    [SerializeField] private TextMeshProUGUI fpsCounter;
    [SerializeField] private TextMeshProUGUI replayFpsCounter;

    private bool showFPS;
    private bool showReplayFPS => showFPS && ReplayManager.IsReplayMode && UIStateManager.CurrentState == UIState.Previewer;

    private float currentFramerate => 1f / Time.deltaTime;

    private int checkedFrameCount;
    private float timeSinceFramerateUpdate;
    private int cachedReplayFPS = int.MinValue;


    private void SetCountersActive()
    {
        fpsCounter.gameObject.SetActive(showFPS);
        replayFpsCounter.gameObject.SetActive(showReplayFPS);

        checkedFrameCount = 0;
        timeSinceFramerateUpdate = 0f;
        cachedReplayFPS = int.MinValue;
    }


    private void UpdateReplayMode(bool replayMode) => SetCountersActive();


    private void UpdateUIState(UIState newState) => SetCountersActive();


    private void UpdateSettings(string setting)
    {
        if(setting == "all" || setting == "fpscounter")
        {
            showFPS = SettingsManager.GetBool("fpscounter");
            SetCountersActive();
        }
    }


    private void UpdateBeat(float beat)
    {
        if(showReplayFPS)
        {
            int averageFPS = PlayerPositionManager.AverageFPS;
            if(cachedReplayFPS != averageFPS)
            {
                cachedReplayFPS = averageFPS;
                replayFpsCounter.text = "Replay: " + averageFPS;
            }
        }
    }


    private void Update()
    {
        if(showFPS)
        {
            timeSinceFramerateUpdate += Time.deltaTime;
            checkedFrameCount++;

            if(timeSinceFramerateUpdate >= FramerateSampleTime)
            {
                float averageFramerate = 1f / (timeSinceFramerateUpdate / checkedFrameCount);
                int fps = Mathf.RoundToInt(averageFramerate);
                fpsCounter.text = "FPS: " + fps;

                checkedFrameCount = 0;
                timeSinceFramerateUpdate = 0f;
            }
        }
    }


    private void Start()
    {
        SettingsManager.OnSettingsUpdated += UpdateSettings;
        ReplayManager.OnReplayModeChanged += UpdateReplayMode;
        UIStateManager.OnUIStateChanged += UpdateUIState;
        TimeManager.OnBeatChanged += UpdateBeat;

        if(SettingsManager.Loaded)
        {
            UpdateSettings("all");
        }
    }
}
