using System.Globalization;
using BlindsDown.Core.Devices;
using BlindsDown.Core.Locking;
using BlindsDown.Tests.Core.Fakes;

namespace BlindsDown.Tests.Core.Locking;

/// <summary>
/// Random histories of locks, unlocks, plugging, unplugging, disabling, other apps changing the mute,
/// missed notifications, clean exits and crashes at random points (including during recovery).
/// A simple model tracks what each device's mute should be: its state before any lock, updated only
/// by changes made by others while unlocked (or to excluded devices, which BlindsDown never guards).
/// Invariants:
///  * while unlocked, every device that can be read has exactly the mute the model expects;
///  * while locked, every usable, readable, non-excluded device is muted and the lock is complete;
///  * excluded and disabled devices are never touched;
///  * at the end, with everything plugged back in, every device matches the model.
/// </summary>
public sealed class LockEnginePropertyTests
{
    private const int Sequences = 500;
    private const int StepsPerSequence = 60;

    [Fact]
    public void RandomHistoriesAlwaysEndWhereTheUserLeftTheDevices()
    {
        for (int seed = 1; seed <= Sequences; seed++)
        {
            new Simulation(seed).Run();
        }
    }

    private enum Kind
    {
        /// <summary>Disappears (NotPresent) when unplugged.</summary>
        Usb,

        /// <summary>Becomes Unplugged and stays readable.</summary>
        Jack,

        /// <summary>Becomes Unplugged and can't be opened.</summary>
        Bluetooth,
    }

    private sealed class Simulation(int seed)
    {
        private readonly Random _rng = new(seed);
        private readonly LockWorld _world = new();
        private readonly Dictionary<string, bool> _expected = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Kind> _kinds = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _trace = [];
        private int _nextId;

        private FakeMicBackend Mics => _world.Mics;

        private LockEngine Engine => _world.Engine;

        public void Run()
        {
            int devices = _rng.Next(1, 6);
            for (int i = 0; i < devices; i++)
            {
                AddDevice(notify: false);
            }

            _world.Engine.Initialize();
            for (int step = 0; step < StepsPerSequence; step++)
            {
                _world.Clock.AdvanceSeconds(_rng.Next(0, 40));
                bool missNotifications = _rng.Next(10) == 0;
                int crashAt = _rng.Next(8) == 0 ? _rng.Next(1, 30) : 0;
                if (crashAt > 0)
                {
                    _world.Crash.ArmAt(crashAt);
                }

                try
                {
                    Step();
                    if (missNotifications)
                    {
                        Mics.DropNotifications();
                        Log("(notifications missed)");
                    }

                    Settle();
                }
                catch (SimulatedCrashException)
                {
                    Log($"CRASH at {_world.Crash.LastCrash}");
                    RestartAndRecover();
                    Settle();
                }

                _world.Crash.Disarm();
                CheckInvariants();
            }

            Finish();
        }

        private void Step()
        {
            List<FakeMic> present = Mics.All.Where(m => m.State is EndpointState.Active).ToList();
            List<FakeMic> away = Mics.All.Where(m => m.State is EndpointState.NotPresent or EndpointState.Unplugged).ToList();
            List<FakeMic> disabled = Mics.All.Where(m => m.State is EndpointState.Disabled).ToList();
            List<FakeMic> readable = Mics.All.Where(m => m.IsReadable).ToList();

            switch (_rng.Next(100))
            {
                case < 15 when !Engine.HasLock:
                    Log("Lock");
                    Engine.Lock();
                    break;
                case < 27 when Engine.HasLock:
                    Log("Unlock");
                    Engine.Unlock();
                    break;
                case < 40 when present.Count > 0:
                    FakeMic leaving = Pick(present);
                    EndpointState to = _kinds[leaving.Id] == Kind.Usb ? EndpointState.NotPresent : EndpointState.Unplugged;
                    Log($"Unplug {leaving.Id} -> {to}");
                    Mics.Unplug(leaving.Id, to);
                    break;
                case < 52 when away.Count > 0:
                    FakeMic back = Pick(away);
                    Log($"Plug {back.Id}");
                    Mics.Plug(back.Id);
                    break;
                case < 56 when present.Count > 0:
                    FakeMic off = Pick(present);
                    Log($"Disable {off.Id}");
                    Mics.Disable(off.Id);
                    break;
                case < 60 when disabled.Count > 0:
                    FakeMic on = Pick(disabled);
                    Log($"Enable {on.Id}");
                    Mics.Enable(on.Id);
                    break;
                case < 80 when readable.Count > 0:
                    FakeMic target = Pick(readable);
                    bool muted = _rng.Next(2) == 0;
                    Log($"Other app sets {target.Id} muted={muted}");
                    Mics.OtherAppSetsMute(target.Id, muted);
                    if (!Engine.HasLock || _excluded.Contains(target.Id))
                    {
                        _expected[target.Id] = muted;
                    }

                    break;
                case < 84 when Mics.All.Count < 8:
                    AddDevice(notify: true);
                    break;
                case < 88:
                    Log("Reconcile");
                    Engine.Reconcile();
                    break;
                case < 92:
                    Log("Killed between operations");
                    RestartAndRecover();
                    break;
                case < 95:
                    Log("Clean exit");
                    Engine.MarkCleanExit();
                    StartupRecovery? recovery = _world.Restart();
                    Assert.True(recovery is null || recovery.ExitedCleanly, Describe("clean exit resumed as a crash"));
                    break;
                case < 98 when Engine.HasLock:
                    FlakyWhileLocked(Pick([.. Mics.All]));
                    break;
                default:
                    Log("Idle");
                    break;
            }
        }

        /// <summary>
        /// A device misbehaves for a moment while locked (reads fail, mutes fail or don't stick) just as it
        /// connects or something unmutes it. Once it behaves again, the safety poll must finish the lock, and
        /// what unlock restores must still be the device's true original state.
        /// </summary>
        private void FlakyWhileLocked(FakeMic mic)
        {
            int failure = _rng.Next(3);
            Log($"Flaky {mic.Id}: {(failure switch { 0 => "reads fail", 1 => "mutes fail", _ => "mutes don't stick" })}");
            SetFailure(mic, failure, on: true);
            try
            {
                if (mic.State is EndpointState.NotPresent or EndpointState.Unplugged)
                {
                    Log($"Plug {mic.Id}");
                    Mics.Plug(mic.Id);
                }
                else if (mic.IsReadable)
                {
                    Log($"Other app sets {mic.Id} muted=False");
                    Mics.OtherAppSetsMute(mic.Id, false);
                    if (_excluded.Contains(mic.Id))
                    {
                        _expected[mic.Id] = false;
                    }
                }

                Settle();
            }
            finally
            {
                SetFailure(mic, failure, on: false);
                Log($"{mic.Id} behaves again");
            }
        }

        private static void SetFailure(FakeMic mic, int failure, bool on)
        {
            switch (failure)
            {
                case 0:
                    mic.GetMuteThrows = on;
                    break;
                case 1:
                    mic.SetMuteThrows = on;
                    break;
                default:
                    mic.MuteDoesNotStick = on;
                    break;
            }
        }

        /// <summary>The app restarts after a crash and the user answers the recovery prompt (randomly).
        /// The recovery itself may crash too.</summary>
        private void RestartAndRecover()
        {
            for (int attempt = 0; ; attempt++)
            {
                int crashAt = attempt < 3 && _rng.Next(5) == 0 ? _rng.Next(1, 20) : 0;
                try
                {
                    StartupRecovery? recovery = _world.Restart(crashAt);
                    if (recovery is null || recovery.ExitedCleanly)
                    {
                        Log($"Restarted ({(recovery is null ? "nothing to recover" : "resumed quietly")})");
                    }
                    else if (recovery.Phase == LockPhase.Locked && _rng.Next(2) == 0)
                    {
                        Log("Restarted: stay locked");
                    }
                    else if (recovery.Phase != LockPhase.Locked && _rng.Next(2) == 0)
                    {
                        Log($"Restarted in {recovery.Phase}: finish the lock");
                        Engine.ResumeLock();
                    }
                    else
                    {
                        Log($"Restarted in {recovery.Phase}: restore devices");
                        Engine.Unlock();
                    }

                    _world.Crash.Disarm();
                    return;
                }
                catch (SimulatedCrashException)
                {
                    Log($"CRASH during recovery at {_world.Crash.LastCrash}");
                }
            }
        }

        /// <summary>What the engine thread does next: deliver notifications, then the safety poll.</summary>
        private void Settle()
        {
            _world.Pump();
            Engine.Reconcile();
            _world.Pump();
        }

        private void CheckInvariants()
        {
            foreach (FakeMic mic in Mics.All.Where(m => _excluded.Contains(m.Id)))
            {
                Assert.True(mic.Muted == _expected[mic.Id], Describe($"excluded {mic} was changed"));
            }

            if (Engine.HasLock)
            {
                Assert.True(Engine.Status.State == LockState.Locked, Describe($"lock is {Engine.Status.State}"));
                foreach (FakeMic mic in Mics.All.Where(m => m.IsReadable && !_excluded.Contains(m.Id)))
                {
                    Assert.True(mic.Muted, Describe($"{mic} is not muted while locked"));
                }
            }
            else
            {
                foreach (FakeMic mic in Mics.All.Where(m => m.IsReadable))
                {
                    Assert.True(mic.Muted == _expected[mic.Id], Describe($"{mic} should be {(_expected[mic.Id] ? "muted" : "unmuted")}"));
                }
            }

            foreach ((string id, bool _, EndpointState state) in Mics.SetMuteLog)
            {
                Assert.False(_excluded.Contains(id), Describe($"excluded {id} was touched"));
                Assert.True(state is EndpointState.Active or EndpointState.Unplugged, Describe($"{id} was touched while {state}"));
            }
        }

        private void Finish()
        {
            if (Engine.HasLock)
            {
                Log("Final unlock");
                Engine.Unlock();
            }

            foreach (FakeMic mic in Mics.All.Where(m => m.State != EndpointState.Active))
            {
                Log($"Final plug {mic.Id}");
                Mics.SetState(mic.Id, EndpointState.Active);
            }

            Settle();
            CheckInvariants();
            Assert.True(_world.Pending.Load().Count == 0, Describe("restores still pending at the end"));
        }

        private void AddDevice(bool notify)
        {
            string id = "m" + (_nextId++).ToString(CultureInfo.InvariantCulture);
            var kind = (Kind)_rng.Next(3);
            bool muted = _rng.Next(5) < 2;
            EndpointState state = _rng.Next(20) switch
            {
                < 12 => EndpointState.Active,
                < 17 => kind == Kind.Usb ? EndpointState.NotPresent : EndpointState.Unplugged,
                _ => EndpointState.Disabled,
            };

            Mics.Add(id, muted, state, unreadableWhenUnplugged: kind == Kind.Bluetooth, hardwareMute: _rng.Next(2) == 0, notify: notify);
            _kinds[id] = kind;
            _expected[id] = muted;
            if (_rng.Next(7) == 0)
            {
                _excluded.Add(id);
                _world.Exclude(id);
            }

            Log($"Add {Mics[id]} {kind}{(_excluded.Contains(id) ? " excluded" : string.Empty)}");
        }

        private FakeMic Pick(List<FakeMic> mics) => mics[_rng.Next(mics.Count)];

        private void Log(string what) => _trace.Add(what);

        private string Describe(string problem) =>
            $"Seed {seed}: {problem}.{Environment.NewLine}Devices: {string.Join(", ", Mics.All)}{Environment.NewLine}"
            + $"Expected: {string.Join(", ", _expected.Select(kv => $"{kv.Key}={(kv.Value ? "muted" : "unmuted")}"))}{Environment.NewLine}"
            + $"Trace:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", _trace)}";
    }
}
