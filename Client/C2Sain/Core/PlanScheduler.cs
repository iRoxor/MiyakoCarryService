// PlanScheduler.cs -- when a squad's guard plan is made, kept, carried on or dropped (fork v12).
//
// One instance per squad, owned by C2Sain/TeamModes.cs (its Entry): GetGuardPost calls Request each time
// a carry bot asks for its post, and TeamModes.Pump reports back when a job finishes (Delivered), fails
// (Faulted) or is given up (AbandonJob). Unity-free so the logic harness
// (.for-claude-to-reference/harnesses/mcs_fork_logic) can drive it with scripted times and positions.
// Reads and writes no files.
//
// Until fork v11 GetGuardPost made the plan on the spot, in the frame that asked: when there was no plan
// yet, a contact report arrived or ran out, the number of living bots changed, or the player had moved
// 4 m from where the last plan was made. Since v12 the plan is made a few milliseconds per frame
// (Core/PlanSlicer.cs), so a request can find a plan in force, a job under way, or both. Rules:
//   - a plan in force that suits the request (same report, same bot count, the player within
//     ReplanMove of where it was made) is used, and any job in flight is dropped as not needed;
//   - otherwise a job is started, unless one is already under way for the same report and bot count,
//     which carries on however far the player has moved since (the next request replaces its plan);
//   - a job whose report or bot count no longer matches is started again;
//   - while a job runs, bots keep the plan in force (or, with none, follow the player: a null post);
//   - a job nobody has asked about for JobIdleSeconds (the squad left the guard layer) is given up;
//   - a job that threw is not retried for FaultPauseSeconds.

using System;

namespace MiyakoCarryService.Client.C2Sain.Core
{
    /// <summary>What a guard plan was made for: the contact report it faces (0 for none) and how many bots it posts.</summary>
    public readonly struct PlanKey : IEquatable<PlanKey>
    {
        public readonly int ReportId;
        public readonly int BotCount;

        public PlanKey(int reportId, int botCount)
        {
            ReportId = reportId;
            BotCount = botCount;
        }

        public bool Equals(PlanKey other)
        {
            return ReportId == other.ReportId && BotCount == other.BotCount;
        }

        public override bool Equals(object obj)
        {
            return obj is PlanKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return ReportId * 397 ^ BotCount;
        }
    }

    /// <summary>What <see cref="PlanScheduler.Request"/> decided.</summary>
    public enum PlanRequest
    {
        /// <summary>The plan in force suits; serve posts from it.</summary>
        UsePlan,

        /// <summary>No job was under way: begin one.</summary>
        StartJob,

        /// <summary>A job for this report and bot count is under way: let it run.</summary>
        ContinueJob,

        /// <summary>The job under way was made for something else: drop it and begin another.</summary>
        RestartJob,

        /// <summary>A plan is needed but a recent failure keeps new jobs paused: serve the plan in force, if any.</summary>
        Paused,
    }

    /// <summary>Tunables for <see cref="PlanScheduler"/>; the plugin uses the defaults.</summary>
    public sealed class PlanScheduleSettings
    {
        /// <summary>Metres the player may move from where a plan was made before it is made again (fork v3-v11: 4).</summary>
        public float ReplanMove = 4f;

        /// <summary>Seconds without a request after which a job under way is given up.</summary>
        public float JobIdleSeconds = 2f;

        /// <summary>Seconds after a job fails before another may start.</summary>
        public float FaultPauseSeconds = 5f;

        /// <summary>True (the default): a job made for another report or bot count is dropped and begun again.</summary>
        public bool RestartOnChange = true;

        /// <summary>
        /// False (the default): a job carries on however far the player moves while it runs. True: it is
        /// begun again whenever the player is more than ReplanMove from where it began, so a player who
        /// keeps moving never gets a finished plan.
        /// </summary>
        public bool RestartOnMove = false;
    }

    /// <summary>Per-squad plan and job bookkeeping; see the file header. Main thread only.</summary>
    public sealed class PlanScheduler
    {
        private bool _hasPlan;
        private PlanKey _planKey;
        private V3 _planAnchor;
        private bool _hasJob;
        private PlanKey _jobKey;
        private V3 _jobAnchor;
        private float _lastRequest;
        private float _pausedUntil = float.NegativeInfinity;

        /// <summary>True once a plan has been delivered (it may be out of date).</summary>
        public bool HasPlan => _hasPlan;

        /// <summary>True while a job is under way.</summary>
        public bool HasJob => _hasJob;

        /// <summary>What the job under way was begun for. Meaningful only while <see cref="HasJob"/>.</summary>
        public PlanKey JobKey => _jobKey;

        /// <summary>Where the player stood when the job under way began. Meaningful only while <see cref="HasJob"/>.</summary>
        public V3 JobAnchor => _jobAnchor;

        /// <summary>
        /// A carry bot asks for its post. Decides what to do about the plan and records the request; when
        /// the answer is StartJob or RestartJob the caller must begin a job for <paramref name="wanted"/> at
        /// <paramref name="player"/>. After any answer, a caller holding a job object that this class no
        /// longer counts (HasJob false) must drop it.
        /// </summary>
        /// <param name="wanted">The report and bot count the post is wanted for.</param>
        /// <param name="player">The player's position.</param>
        /// <param name="now">Game time in seconds.</param>
        /// <param name="settings">Tunables; read on every call.</param>
        public PlanRequest Request(PlanKey wanted, V3 player, float now, PlanScheduleSettings settings)
        {
            _lastRequest = now;
            if (_hasPlan && _planKey.Equals(wanted) && V3.Distance(_planAnchor, player) <= settings.ReplanMove)
            {
                _hasJob = false;
                return PlanRequest.UsePlan;
            }
            if (now < _pausedUntil)
            {
                return PlanRequest.Paused;
            }
            if (_hasJob)
            {
                var changed = settings.RestartOnChange && !_jobKey.Equals(wanted);
                var moved = settings.RestartOnMove && V3.Distance(_jobAnchor, player) > settings.ReplanMove;
                if (!changed && !moved)
                {
                    return PlanRequest.ContinueJob;
                }
                Begin(wanted, player);
                return PlanRequest.RestartJob;
            }
            Begin(wanted, player);
            return PlanRequest.StartJob;
        }

        /// <summary>The job under way finished: its plan is now the plan in force.</summary>
        public void Delivered()
        {
            if (!_hasJob)
            {
                return;
            }
            _hasPlan = true;
            _planKey = _jobKey;
            _planAnchor = _jobAnchor;
            _hasJob = false;
        }

        /// <summary>The job under way threw: drop it and hold off new jobs for FaultPauseSeconds.</summary>
        public void Faulted(float now, PlanScheduleSettings settings)
        {
            _hasJob = false;
            _pausedUntil = now + settings.FaultPauseSeconds;
        }

        /// <summary>True when a job is under way but nobody has asked for a post for JobIdleSeconds.</summary>
        public bool IsIdle(float now, PlanScheduleSettings settings)
        {
            return _hasJob && now - _lastRequest > settings.JobIdleSeconds;
        }

        /// <summary>Gives up the job under way (IsIdle); the plan in force stays.</summary>
        public void AbandonJob()
        {
            _hasJob = false;
        }

        private void Begin(PlanKey wanted, V3 player)
        {
            _hasJob = true;
            _jobKey = wanted;
            _jobAnchor = player;
        }
    }
}
