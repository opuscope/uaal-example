using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Opuscope.Bridge;
using UniRx;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR

using UnityEditor;

[CustomEditor(typeof(UnityBridgeDemo))] 
public class UnityBridgeDemoEditor : Editor 
{
    public override void OnInspectorGUI() 
    {
        UnityBridgeDemo demo = (UnityBridgeDemo) target;
        
        DrawDefaultInspector();
        if (GUILayout.Button("Test Cancellation"))
        {
            demo.RunCancellationTest();
        }
    }
}
#endif


class NativeBridge
{
    private const string INTERNAL = "__Internal";
#if UNITY_IOS
    [DllImport(INTERNAL)]
    public static extern void sendMessage(string path, string content);
#endif
}

class DummyBridgeMessenger : IBridgeMessenger
{
    public void SendMessage(string path, string content)
    {
        Debug.Log($"{this} {nameof(SendMessage)} to {path} : {content}");
    }
}

class NativeBridgeMessenger : IBridgeMessenger
{
    public void SendMessage(string path, string content)
    {
        Debug.Log($"{this} {nameof(SendMessage)} to {path} : {content}");
#if UNITY_IOS
        NativeBridge.sendMessage(path, content);
#else
        throw new NotImplementedException();  
#endif
    }
}

public class UnityBridgeDemo : MonoBehaviour
{
    private const string TEST_SEPARATOR = "\n----------------------------\n\n";
    
    private BroadcastingBridgeListener _bridgeListener;
    private Bridge _bridge;
    private BridgeWorkflowPerformer _workflowPerformer;
    private BridgeWorkflowRegister _workflowRegister;

    private readonly CompositeDisposable _subscriptions = new();

    private int _counter = 0;
    
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    class TestPayload
    {
        public string Name;
        public int Number;
        public double Duration;

        public override string ToString()
        {
            return $"{GetType().Name} {nameof(Name)} {Name} {nameof(Number)} {Number} {nameof(Duration)} {Duration}";
        }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    class TestResult
    {
        public string Message;
        public int Processed;
        
        public override string ToString()
        {
            return $"{GetType().Name} {nameof(Message)} {Message} {nameof(Processed)} {Processed}";
        }
    }


    private struct Paths
    {
        public const string StartTest = "/test/start";
    }
    
    private struct Procedures
    {
        public const string ImmediateGreeting = "/greeting/immediate";
        public const string DelayedGreeting = "/greeting/delayed";
        public const string ErrorGreeting = "/greeting/error";
    }

    protected void Awake()
    {
        // avoid excessive logging ios side
        Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.None);
        
        _bridgeListener = new BroadcastingBridgeListener();

        IBridgeMessenger messenger =
#if UNITY_EDITOR
            new DummyBridgeMessenger();
#else
            new NativeBridgeMessenger();
#endif
            

        _bridge = new Bridge(messenger, _bridgeListener);
        _workflowPerformer = new BridgeWorkflowPerformer(_bridge);
        _workflowRegister = new BridgeWorkflowRegister(_bridge);
        
        _workflowRegister.Register<TestPayload, TestResult>(Procedures.ImmediateGreeting, payload =>
        {
            return new TestResult
            {
                Message = $"Hello {payload.Name}", 
                Processed = payload.Number + 100
            };
        });
        
        _workflowRegister.Register<TestPayload, TestResult>(Procedures.DelayedGreeting, async (payload, token) =>
        {
            await UniTask.Delay(TimeSpan.FromSeconds(payload.Duration), cancellationToken: token);
            return new TestResult
            {
                Message = $"Hello {payload.Name}", 
                Processed = payload.Number + 100
            };
        });
        
        _workflowRegister.Register<TestPayload, TestResult>(Procedures.ErrorGreeting, async (payload, token) =>
        {
            await UniTask.Delay(TimeSpan.FromSeconds(payload.Duration), cancellationToken: token);
            throw new Exception("Error Greeting");
        });
    }

    protected void OnEnable()
    {
        _subscriptions.Add(_bridge.Publish(Paths.StartTest).Subscribe(_ =>
        {
            RunAll().Forget();
        }));
    }

    protected void OnDisable()
    {
        _subscriptions.Clear();
    }

    public void OnBridgeMessage(string message)
    {
        BridgeMessage bridgeMessage = JsonConvert.DeserializeObject<BridgeMessage>(message);
        if (bridgeMessage != null)
        {
            Debug.Log($"{GetType().Name} {nameof(OnBridgeMessage)} {bridgeMessage}");
            _bridgeListener.Broadcast(bridgeMessage);
        }
    }

    public void RunCancellationTest()
    {
        TestCancelledWorkflow().Forget();
    }
    
    private async UniTask RunAll()
    {
        try
        {
            Debug.Log(TEST_SEPARATOR);
            await TestImmediateWorkflow();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Debug.Log(TEST_SEPARATOR);
            await TestDelayedWorkflow();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Debug.Log(TEST_SEPARATOR);
            await TestConcurrentWorkflow();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Debug.Log(TEST_SEPARATOR);
            await TestCancelledWorkflow();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Debug.Log(TEST_SEPARATOR);
            await TestErrorWorkflow();
            await Task.Delay(TimeSpan.FromSeconds(1));
            Debug.Log(TEST_SEPARATOR);
        }
        catch (Exception e)
        {
            Debug.LogError($"{nameof(RunAll)} unexpected exception {e}");
        }
    }

    private async UniTask TestImmediateWorkflow()
    {
        // note : duration is not taken into account
        TestPayload payload = new TestPayload {Name = "Gertrude", Number = _counter++, Duration = 1000};
        Debug.Log($"{GetType().Name} {nameof(TestImmediateWorkflow)} payload {payload}");
        TestResult result = await _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.ImmediateGreeting, payload, CancellationToken.None);
        Debug.Log($"{GetType().Name} {nameof(TestImmediateWorkflow)} result {result}");
    }
    
    private async UniTask TestDelayedWorkflow()
    {
        TestPayload payload = new TestPayload {Name = "Norbert", Number = _counter++, Duration = 5};
        Debug.Log($"{GetType().Name} {nameof(TestDelayedWorkflow)} payload {payload}");
        TestResult result = await _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.DelayedGreeting, payload, CancellationToken.None);
        Debug.Log($"{GetType().Name} {nameof(TestDelayedWorkflow)} result {result}");
    }
    
    private async UniTask TestConcurrentWorkflow()
    {
        TestPayload payload1 = new TestPayload {Name = "Brigitte", Number = _counter++, Duration = 2};
        TestPayload payload2 = new TestPayload {Name = "Norbert", Number = _counter++, Duration = 5};
        TestPayload payload3 = new TestPayload {Name = "Gertrude", Number = _counter++, Duration = 4};
        
        Debug.Log($"{GetType().Name} {nameof(TestConcurrentWorkflow)} payloads {payload1} {payload2} {payload3}");
        
        UniTask<TestResult> task1 = _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.DelayedGreeting, payload1, CancellationToken.None);
        UniTask<TestResult> task2 = _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.DelayedGreeting, payload2, CancellationToken.None);
        UniTask<TestResult> task3 = _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.DelayedGreeting, payload3, CancellationToken.None);

        TestResult result1 = await task1;
        TestResult result2 = await task2;
        TestResult result3 = await task3;
        
        Debug.Log($"{GetType().Name} {nameof(TestConcurrentWorkflow)} results {result1} {result2} {result3}");
    }

    private async UniTask TestCancelledWorkflow()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        TestPayload payload = new TestPayload {Name = "Norbert", Number = _counter++, Duration = 5};
        Debug.Log($"{GetType().Name} {nameof(TestCancelledWorkflow)} payload {payload}");
        CancellationTokenSource source = new CancellationTokenSource();
        source.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            TestResult result = await _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.DelayedGreeting, payload, source.Token);
            Debug.LogError($"{GetType().Name} {nameof(TestCancelledWorkflow)} unexpected result {result} after {stopwatch.ElapsedMilliseconds} ms cancelled is {source.Token.IsCancellationRequested}");
        }
        catch (OperationCanceledException e)
        {
            Debug.Log($"{GetType().Name} {nameof(TestCancelledWorkflow)} expected cancellation occured after {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            Debug.LogError($"{GetType().Name} {nameof(TestCancelledWorkflow)} unexpected error {e.GetType().Name} {e.Message} after {stopwatch.ElapsedMilliseconds} ms");
        }
    }

    private async UniTask TestErrorWorkflow()
    {
        TestPayload payload = new TestPayload {Name = "Norbert", Number = _counter++, Duration = 5};
        Debug.Log($"{GetType().Name} {nameof(TestErrorWorkflow)} payload {payload}");
        try
        {
            TestResult result = await _workflowPerformer.Perform<TestPayload, TestResult>(Procedures.ErrorGreeting, payload, CancellationToken.None);
            Debug.LogError($"{GetType().Name} {nameof(TestErrorWorkflow)} unexpected result {result}");
        }
        catch (RuntimeWorkflowException e)
        {
            Debug.Log($"{GetType().Name} {nameof(TestErrorWorkflow)} expected error");
        }
        catch (Exception e)
        {
            Debug.LogError($"{GetType().Name} {nameof(TestErrorWorkflow)} unexpected error {e.GetType().Name} {e.Message}");
        }
    }
}
