using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Clock = System.Diagnostics.Stopwatch;
namespace YC.Presentation.Validation
{
    // 显式本地诊断：只观察原同步提交，不保存命令、状态图或结果。
    [DefaultExecutionOrder(-32000)]
    public sealed class QY018MonoTrace : MonoBehaviour
    {
        [Serializable] sealed class Observation
        {
            public string utc,reason,unity,file,gcMode,gameId,commandId,previousLog,restoredLog;
            public bool debugBuild,callstacks,incremental,active,profilerEnabled,binaryLog,firstFrameMayBePartial;
            public bool settingsRestored,previousEnabled,previousBinary,previousStacks;
            public int pid,frame,firstFrame,window,submitted,revision,ownerThread,gcCount,maxGeneration,bufferBytes,previousBuffer;
            public long tick,frequency,managedApproximate,monoUsed,monoReserved,unityUsed,unityReserved,fileBytes,capturedTotalBytes;
        }
        static QY018MonoTrace instance;
        string basePath,path,previousLog;
        int firstFrame,previousBuffer,windows,submitted,ownerThread;
        bool active,stopped,previousStacks,previousEnabled,previousBinary;
        IDisposable traceScope;
        long capturedBytes;
        const long MaximumCaptureBytes=10737418240L;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void StartIfRequested()
        {
            var file=Environment.GetCommandLineArgs().FirstOrDefault(value=>value.StartsWith("--yc-mono-trace-file=",StringComparison.Ordinal));
            if(file==null || instance!=null)return;
            if(!Debug.isDebugBuild){Debug.LogError("Mono 分配诊断需要 Development Build。");return;}
            string target=Path.GetFullPath(file.Substring("--yc-mono-trace-file=".Length));
            if(File.Exists(target+".jsonl")){Debug.LogError("本地Profiler记录已存在，拒绝覆盖。");return;}
            var component=new GameObject("Mono 分配诊断").AddComponent<QY018MonoTrace>();
            DontDestroyOnLoad(component.gameObject);component.basePath=target;
            component.ownerThread=Thread.CurrentThread.ManagedThreadId;instance=component;component.Record("initialized");
        }
        // 缺显式参数时没有scope、Profiler调用或诊断对象；返回默认值类型。
        public static CommandScope ObserveCommand(string gameId,string commandId,int revision)
        {
            var value=instance;
            if(value==null || value.stopped || Thread.CurrentThread.ManagedThreadId!=value.ownerThread)return default;
            try{return value.BeginCommand(gameId,commandId,revision);}
            catch(Exception error){value.Fail(error);return default;}
        }
        public readonly struct CommandScope:IDisposable
        {
            readonly QY018MonoTrace owner;
            readonly string gameId,commandId;
            readonly int revision;
            internal CommandScope(QY018MonoTrace owner,string gameId,string commandId,int revision)
            {this.owner=owner;this.gameId=gameId;this.commandId=commandId;this.revision=revision;}
            public void Dispose()
            {
                if(owner==null)return;
                try{Profiler.EndSample();owner.Record("command.end",gameId,commandId,revision);}
                catch(Exception error){owner.Fail(error);}
            }
        }
        CommandScope BeginCommand(string gameId,string commandId,int revision)
        {
            submitted++;
            if(!active && windows<3 && submitted>=(windows==0?8:windows==1?24:48))StartWindow();
            if(!active)return default;
            Record("command.begin",gameId,commandId,revision);
            Profiler.BeginSample("QY004.RealSubmit/"+commandId+"/frame="+Time.frameCount);
            return new CommandScope(this,gameId,commandId,revision);
        }
        void StartWindow()
        {
            if(Profiler.enabled || Profiler.enableBinaryLog){Record("已有Profiler记录，拒绝覆盖");stopped=true;return;}
            if(capturedBytes>=MaximumCaptureBytes){Record("捕获总量达到上限");stopped=true;return;}
            path=basePath+".w"+(windows+1)+".raw";
            if(File.Exists(path))throw new IOException("本地Profiler原件已存在，拒绝覆盖："+path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            previousEnabled=Profiler.enabled;previousBinary=Profiler.enableBinaryLog;
            previousBuffer=Profiler.maxUsedMemory;previousLog=Profiler.logFile;previousStacks=Profiler.enableAllocationCallstacks;
            firstFrame=Time.frameCount;Record("window.before");
            // 首帧由实际同步提交触发，可能有部分帧边缘；随后按真实帧号推进三帧。
            // 不用毫秒等待猜动作，不把一个同步长帧拆成多个帧。
            active=true;Profiler.maxUsedMemory=536870912;Profiler.logFile=path;
            Profiler.enableAllocationCallstacks=false;Profiler.enableBinaryLog=true;Profiler.enabled=true;
            traceScope=YC.Domain.State.GameStateCloneService.BeginTimingScope(IgnoreTiming,TraceSample);
            FrameMarker();Record("window.open");
        }
        void Update()
        {
            if(!active)return;
            try{if(Time.frameCount-firstFrame>=4)Stop("four_frame_boundaries");else FrameMarker();}
            catch(Exception error){Fail(error);}
        }
        static void FrameMarker(){Profiler.BeginSample("QY004.PlayerFrame/"+Time.frameCount);Profiler.EndSample();}
        void Stop(string reason)
        {
            if(!active)return;
            try
            {
                Profiler.enabled=false;traceScope?.Dispose();traceScope=null;
                Profiler.enableBinaryLog=previousBinary;Profiler.enableAllocationCallstacks=previousStacks;
                Profiler.logFile=previousLog;Profiler.maxUsedMemory=previousBuffer;Profiler.enabled=previousEnabled;
            }
            finally
            {
                active=false;windows++;
                if(File.Exists(path))capturedBytes+=new FileInfo(path).Length;
                if(windows>=3 || capturedBytes>=MaximumCaptureBytes)stopped=true;
                Record(reason);
            }
        }
        void Fail(Exception error)
        {
            try{Stop("error.restore");}catch(Exception restoreError){Debug.LogError(restoreError);}
            stopped=true;Debug.LogError("本地Mono诊断停止："+error);
        }
        void Record(string reason,string gameId=null,string commandId=null,int revision=-1)
        {
            try
            {
                var value=new Observation{utc=DateTime.UtcNow.ToString("o"),reason=reason,unity=UnityEngine.Application.unityVersion,
                    file=path,debugBuild=Debug.isDebugBuild,callstacks=Profiler.enableAllocationCallstacks,active=active,
                    pid=System.Diagnostics.Process.GetCurrentProcess().Id,frame=Time.frameCount,firstFrame=firstFrame,
                    window=active?windows+1:windows,submitted=submitted,gameId=gameId,commandId=commandId,revision=revision,
                    tick=Clock.GetTimestamp(),frequency=Clock.Frequency,ownerThread=Thread.CurrentThread.ManagedThreadId,
                    profilerEnabled=Profiler.enabled,binaryLog=Profiler.enableBinaryLog,bufferBytes=Profiler.maxUsedMemory,
                    previousBuffer=previousBuffer,previousLog=previousLog,restoredLog=Profiler.logFile,
                    previousEnabled=previousEnabled,previousBinary=previousBinary,previousStacks=previousStacks,
                    settingsRestored=!active && windows>0 && Profiler.enabled==previousEnabled && Profiler.enableBinaryLog==previousBinary &&
                        Profiler.enableAllocationCallstacks==previousStacks && Profiler.maxUsedMemory==previousBuffer && Profiler.logFile==previousLog,
                    firstFrameMayBePartial=true,gcCount=GC.CollectionCount(0),maxGeneration=GC.MaxGeneration,
                    gcMode=GarbageCollector.GCMode.ToString(),incremental=GarbageCollector.isIncremental,
                    managedApproximate=GC.GetTotalMemory(false),monoUsed=Profiler.GetMonoUsedSizeLong(),monoReserved=Profiler.GetMonoHeapSizeLong(),
                    unityUsed=Profiler.GetTotalAllocatedMemoryLong(),unityReserved=Profiler.GetTotalReservedMemoryLong(),
                    fileBytes=path!=null && File.Exists(path)?new FileInfo(path).Length:0,capturedTotalBytes=capturedBytes};
                Directory.CreateDirectory(Path.GetDirectoryName(basePath));
                File.AppendAllText(basePath+".jsonl",JsonUtility.ToJson(value)+"\n");
            }
            catch(Exception error){Debug.LogError("本地Mono诊断记录失败："+error);}
        }
        void OnApplicationQuit(){Stop("application.quit");Record("application.quit");}
        void OnDestroy(){Stop("destroy");if(instance==this)instance=null;}
        static void IgnoreTiming(string phase,long ticks){}
        static void TraceSample(string phase,bool begin){if(begin)Profiler.BeginSample(phase);else Profiler.EndSample();}
    }
}
