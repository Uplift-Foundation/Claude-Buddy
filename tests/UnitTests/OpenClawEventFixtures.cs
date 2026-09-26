namespace ClaudeBuddy.Tests
{
    // Real event sequences off a live OpenClaw gateway (2026.9.2), captured on
    // 25 Sep 2026 with `tools/openclaw-probe -- events`, which subscribes the way
    // the app does (sessions.subscribe and nothing else). One JSON object per
    // line: "t" is seconds since the first row, "name" the event, "payload" the
    // event's payload.
    //
    // Scrubbed, and reduced to the fields the classifier and OnEvent read —
    // sessionKey, runId, phase, reason, stream, state, action, data.phase and
    // task.status/runId/childSessionKey. Every UUID is replaced by a numbered
    // placeholder in order of first appearance, Discord ids likewise, agent ids
    // other than "main" become agent<n>, and the epoch stamps inside task runIds
    // are zeroed. The order, the spacing, and which fields are present or absent
    // on each row are the capture's own; tick/health/heartbeat rows are dropped.
    // The raw capture is not checked in: its payloads carry whole session
    // records, participants and transcript metadata.
    internal static class OpenClawEventFixtures
    {
        // A run on an agent's main session — here the gateway's own heartbeat job
        // injecting into it — as a client on sessions.subscribe sees it: the
        // start and the end, 4.5 s apart, each carrying the runId, and nothing
        // at all in between. This is the whole of what a non-cron run sends.
        internal const string MainSessionRun = """
{"t":0.0,"name":"task","payload":{"action":"upserted","task":{"status":"running","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":0.001,"name":"cron","payload":{"action":"started"}}
{"t":0.132,"name":"sessions.changed","payload":{"sessionKey":"agent:main:main","phase":"message"}}
{"t":0.132,"name":"sessions.changed","payload":{"sessionKey":"agent:main:main","runId":"00000000-0000-4000-8000-000000000003","phase":"start"}}
{"t":4.625,"name":"sessions.changed","payload":{"sessionKey":"agent:main:main","phase":"message"}}
{"t":4.626,"name":"sessions.changed","payload":{"sessionKey":"agent:main:main","runId":"00000000-0000-4000-8000-000000000003","phase":"end"}}
{"t":4.721,"name":"task","payload":{"action":"upserted","task":{"status":"completed","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":4.722,"name":"cron","payload":{"action":"finished"}}
{"t":4.722,"name":"cron","payload":{"action":"scheduled"}}
""";

        // A cron job's run on its own session, the shape the pre-CB-169 design
        // was built from: streaming agent/tool/chat events carrying the runId,
        // closed by lifecycle end, chat final and cron finished. The placement
        // rows at t=0.3 name two runs at once, neither with a phase.
        internal const string CronSessionRun = """
{"t":0.0,"name":"task","payload":{"action":"upserted","task":{"status":"running","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":0.021,"name":"cron","payload":{"action":"started"}}
{"t":0.275,"name":"task","payload":{"action":"upserted","task":{"status":"running","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002","childSessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003"}}}
{"t":0.3,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","reason":"placement"}}
{"t":0.3,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000004","reason":"placement"}}
{"t":1.323,"name":"session.message","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003"}}
{"t":4.558,"name":"session.tool","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"tool","data":{"phase":"start"}}}
{"t":5.234,"name":"session.tool","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"tool","data":{"phase":"result"}}}
{"t":6.751,"name":"agent","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"thinking"}}
{"t":8.17,"name":"agent","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"thinking"}}
{"t":8.995,"name":"agent","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"assistant"}}
{"t":9.018,"name":"chat","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","state":"delta"}}
{"t":9.068,"name":"agent","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"assistant"}}
{"t":10.658,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","phase":"message"}}
{"t":10.813,"name":"agent","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","stream":"lifecycle","data":{"phase":"end"}}}
{"t":10.861,"name":"chat","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","runId":"00000000-0000-4000-8000-000000000003","state":"final"}}
{"t":12.469,"name":"task","payload":{"action":"upserted","task":{"status":"completed","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002","childSessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003"}}}
{"t":12.47,"name":"cron","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000001:run:00000000-0000-4000-8000-000000000003","action":"finished"}}
{"t":12.47,"name":"cron","payload":{"action":"scheduled"}}
""";

        // The CB-152 shape: a cron job's binding update firing sessions.changed
        // for sessions across the roster — a Discord DM and a Discord channel
        // among them — with a reason and no phase or runId. None of these
        // sessions was doing anything.
        internal const string RosterBurst = """
{"t":0.0,"name":"task","payload":{"action":"upserted","task":{"status":"running","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":0.014,"name":"cron","payload":{"action":"started"}}
{"t":2.627,"name":"cron","payload":{"action":"updated"}}
{"t":2.636,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000003","reason":"cron-binding"}}
{"t":3.123,"name":"cron","payload":{"action":"updated"}}
{"t":3.123,"name":"sessions.changed","payload":{"sessionKey":"agent:main:cron:00000000-0000-4000-8000-000000000004","reason":"cron-binding"}}
{"t":3.123,"name":"sessions.changed","payload":{"sessionKey":"agent:main:discord:direct:100000000000000001","reason":"cron-binding"}}
{"t":3.424,"name":"cron","payload":{"action":"updated"}}
{"t":3.428,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000005","reason":"cron-binding"}}
{"t":3.804,"name":"cron","payload":{"action":"updated"}}
{"t":3.804,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000006","reason":"cron-binding"}}
{"t":4.248,"name":"cron","payload":{"action":"updated"}}
{"t":4.248,"name":"sessions.changed","payload":{"sessionKey":"agent:main:cron:00000000-0000-4000-8000-000000000007","reason":"cron-binding"}}
{"t":4.825,"name":"cron","payload":{"action":"updated"}}
{"t":4.825,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:cron:00000000-0000-4000-8000-000000000008","reason":"cron-binding"}}
{"t":4.825,"name":"sessions.changed","payload":{"sessionKey":"agent:agent1:discord:channel:100000000000000002","reason":"cron-binding"}}
{"t":4.854,"name":"task","payload":{"action":"upserted","task":{"status":"completed","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":4.879,"name":"cron","payload":{"action":"finished"}}
{"t":4.879,"name":"cron","payload":{"action":"scheduled"}}
""";

        // Background housekeeping with no run in it: a cron job failing at once,
        // and task records being upserted and deleted. No row names a session at
        // top level — the task's session, where it has one, is childSessionKey.
        internal const string Housekeeping = """
{"t":0.0,"name":"task","payload":{"action":"upserted","task":{"status":"running","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":0.0,"name":"cron","payload":{"action":"started"}}
{"t":0.276,"name":"task","payload":{"action":"upserted","task":{"status":"failed","runId":"cron:00000000-0000-4000-8000-000000000001:0:00000000-0000-4000-8000-000000000002"}}}
{"t":0.276,"name":"cron","payload":{"action":"finished"}}
{"t":0.276,"name":"cron","payload":{"action":"scheduled"}}
{"t":38.902,"name":"task","payload":{"action":"deleted"}}
{"t":38.902,"name":"task","payload":{"action":"deleted"}}
""";
    }
}
