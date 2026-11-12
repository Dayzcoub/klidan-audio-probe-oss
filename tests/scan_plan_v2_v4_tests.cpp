#include <cassert>
#include <iostream>
#include <string>
#include <vector>
#include "scan_plan_v2_v4.h"
#include "scan_plan_v2_v4_transport.h"

#undef assert
#define assert(expr) do { if (!(expr)) { std::cerr << "assertion failed at line " << __LINE__ << ": " #expr << '\n'; return 1; } } while (false)

int Run() {
    using namespace ScanPlanV2V4;
    using namespace ChannelRouting;
    Configuration route;
    route.captureEndpointId = "capture"; route.renderEndpointId = "render";
    route.capture = {{48'000, 2, ExclusiveFormatMatrix::Encoding::Pcm16}, 3};
    route.render = {{48'000, 2, ExclusiveFormatMatrix::Encoding::Pcm16}, 3};
    route.mode = Mode::MonoToStereo; route.captureChannels = {0, 0}; route.renderChannels = {0, 1};
    std::vector<Candidate> candidates;
    for (unsigned i = 0; i < 8; ++i) {
        const auto frames = 144U + i * 48U;
        const auto duration = Duration(frames, 48'000);
        candidates.push_back({i, duration, frames, frames, duration, duration, 0, 0, false, false, 144, 480, 144, 480, ""});
    }
    const PeriodBounds normalBounds{30'000, 100'000, 0, false};
    assert(ComputeBudget(8, 5).operations == 64);
    assert(ComputeBudget(8, 5).activeSeconds == 240);
    assert(ComputeBudget(8, 5).sessionSeconds == 2956);
    const auto repeatBudget = ComputeRepeatSelectedBudget();
    assert(repeatBudget.operations == 7 && repeatBudget.activeSeconds == 25 && repeatBudget.sessionSeconds == 600);
    const std::vector<Candidate> selected{{0, 100'000, 480, 480, 100'000, 100'000, 0, 0, false, false, 144, 480, 144, 480, ""}};
    const auto repeatCanonical = Canonical("capture", "render", route, normalBounds, normalBounds, selected, 5, "PRO_AUDIO", 7, 25, 30, 600, 8, 7, Fingerprint(route), true);
    assert(repeatCanonical.find("|REPEAT_SELECTED|5|7|25|") != std::string::npos);
    TransportSequence repeatSelectedSequence(selected, 7, 25, 5, true);
    repeatSelectedSequence.Begin(0, Step::CaptureInitialize, "r-cap"); repeatSelectedSequence.Complete(Disposition::Clean, true);
    repeatSelectedSequence.Begin(0, Step::RenderInitialize, "r-ren"); repeatSelectedSequence.Complete(Disposition::Clean, true);
    assert(repeatSelectedSequence.RepeatabilityPending());
    repeatSelectedSequence.BeginRepeatability();
    for (unsigned i = 0; i < 5; ++i) {
        repeatSelectedSequence.Begin(0, Step::Repeatability, "r-" + std::to_string(i), 5);
        repeatSelectedSequence.Complete(Disposition::Clean, true);
        if (i < 4) repeatSelectedSequence.BeginRepeatability();
    }
    assert(repeatSelectedSequence.Complete() && repeatSelectedSequence.UsedOperations() == 7 && repeatSelectedSequence.UsedActiveSeconds() == 25);
    const auto canonical = Canonical("capture", "render", route, normalBounds, normalBounds, candidates, 5, "OFF", 64, 240, 30, 2956, 8, 7, Fingerprint(route));
    assert(canonical.starts_with("SCAN_PLAN_V2|4|7|capture|render|"));
    assert(canonical.find("B|30000|100000|ALIGNMENT_UNKNOWN|30000|100000|ALIGNMENT_UNKNOWN\n") != std::string::npos);
    assert(canonical.find("C|0|30000|144|144|30000|30000|0|false|0|false|144|480|144|480|\n") != std::string::npos);
    auto changed = candidates; changed[0].captureFrames++;
    bool rejected = false;
    try { (void)Canonical("capture", "render", route, normalBounds, normalBounds, changed, 5, "OFF", 64, 240, 30, 2956, 8, 7, Fingerprint(route)); } catch (const std::invalid_argument&) { rejected = true; }
    assert(rejected);
    rejected = false;
    try { (void)ComputeBudget(9, 5); } catch (const std::invalid_argument&) { rejected = true; }
    assert(rejected);
    route.capture.format.sampleRate = 44'100;
    const auto requested = 30'000LL;
    const auto captureFrames = Frames(requested, 44'100);
    const auto renderFrames = Frames(requested, 48'000);
    const PeriodBounds mixedCaptureBounds{30'000, 100'000, 0, false};
    const std::vector<Candidate> asymmetric{{0, requested, captureFrames, renderFrames, Duration(captureFrames, 44'100), Duration(renderFrames, 48'000), 0, 0, false, false, 132, 441, 144, 480, ""}};
    const auto asymmetricCanonical = Canonical("capture", "render", route, mixedCaptureBounds, normalBounds, asymmetric, 0, "OFF", 3, 5, 30, 139, 8, 7, Fingerprint(route));
    assert(asymmetricCanonical.find("C|0|30000|132|144|29932|30000|") != std::string::npos);
    auto unicodeRoute = route;
    unicodeRoute.capture.format.sampleRate = 48'000;
    unicodeRoute.captureEndpointId = "capture-кириллица";
    unicodeRoute.renderEndpointId = "render-音声";
    auto unicodeCanonical = Canonical(unicodeRoute.captureEndpointId, unicodeRoute.renderEndpointId, unicodeRoute, normalBounds, normalBounds, candidates, 5, "OFF", 64, 240, 30, 2956, 8, 7, Fingerprint(unicodeRoute));
    const std::string expected = "SCAN_PLAN_V2|4|7|capture-кириллица|render-音声|d2e58c0f94fa7718|5|64|240|30|2956|8|\nB|30000|100000|ALIGNMENT_UNKNOWN|30000|100000|ALIGNMENT_UNKNOWN\n"
        "C|0|30000|144|144|30000|30000|0|false|0|false|144|480|144|480|\nC|1|40000|192|192|40000|40000|0|false|0|false|144|480|144|480|\n"
        "C|2|50000|240|240|50000|50000|0|false|0|false|144|480|144|480|\nC|3|60000|288|288|60000|60000|0|false|0|false|144|480|144|480|\n"
        "C|4|70000|336|336|70000|70000|0|false|0|false|144|480|144|480|\nC|5|80000|384|384|80000|80000|0|false|0|false|144|480|144|480|\n"
        "C|6|90000|432|432|90000|90000|0|false|0|false|144|480|144|480|\nC|7|100000|480|480|100000|100000|0|false|0|false|144|480|144|480|\nM|OFF\n";
    assert(unicodeCanonical == expected);
    const auto proAudioCanonical = Canonical(unicodeRoute.captureEndpointId, unicodeRoute.renderEndpointId, unicodeRoute, normalBounds, normalBounds, candidates, 5, "PRO_AUDIO", 64, 240, 30, 2956, 8, 7, Fingerprint(unicodeRoute));
    assert(proAudioCanonical.ends_with("M|PRO_AUDIO\n") && proAudioCanonical != canonical);

    TransportSequence sequence(candidates, 24, 40);
    bool orderRejected = false;
    try { sequence.Begin(0, Step::Duplex, "duplex-first", 5); } catch (const std::invalid_argument&) { orderRejected = true; }
    assert(orderRejected && sequence.UsedOperations() == 0);
    sequence.Begin(0, Step::CaptureInitialize, "cap-0"); sequence.Complete(Disposition::Clean, true);
    bool replayRejected = false;
    try { sequence.Begin(0, Step::RenderInitialize, "cap-0"); } catch (const std::invalid_argument&) { replayRejected = true; }
    assert(replayRejected && sequence.UsedOperations() == 1);
    sequence.Begin(0, Step::RenderInitialize, "ren-0"); sequence.Complete(Disposition::Failed, true);
    assert(sequence.CandidateIndex() == 1 && sequence.ExpectedStep() == Step::CaptureInitialize);
    sequence.Begin(1, Step::CaptureInitialize, "cap-1"); sequence.Complete(Disposition::Clean, true);
    sequence.Begin(1, Step::RenderInitialize, "ren-1"); sequence.Complete(Disposition::Clean, true);
    sequence.Begin(1, Step::Duplex, "duplex-1", 5); sequence.Complete(Disposition::Failed, true);
    assert(sequence.CandidateIndex() == 2 && sequence.UsedActiveSeconds() == 5);
    sequence.Begin(2, Step::CaptureInitialize, "cap-2"); sequence.Complete(Disposition::Unknown, true);
    assert(sequence.Stopped());
    bool stoppedRejected = false;
    try { sequence.Begin(2, Step::CaptureInitialize, "cap-2-retry"); } catch (const std::invalid_argument&) { stoppedRejected = true; }
    assert(stoppedRejected);

    TransportSequence mismatch(candidates, 24, 40);
    mismatch.Begin(0, Step::CaptureInitialize, "cap-mismatch"); mismatch.Complete(Disposition::Clean, true, false);
    assert(mismatch.CandidateIndex() == 1); // A successful Initialize with different actual geometry cannot authorize Duplex.

    TransportSequence bloody(candidates, 29, 65, 5);
    for (unsigned i = 0; i < 3; ++i) {
        bloody.Begin(i, Step::CaptureInitialize, "bc" + std::to_string(i)); bloody.Complete(Disposition::Clean, true);
        bloody.Begin(i, Step::RenderInitialize, "br" + std::to_string(i)); bloody.Complete(Disposition::Clean, true);
        bloody.Begin(i, Step::Duplex, "bd" + std::to_string(i), 5);
        bloody.Complete(i == 2 ? Disposition::Clean : Disposition::Failed, true);
    }
    assert(bloody.Stopped() && bloody.RepeatabilityPending() && bloody.CandidateIndex() == 2);
    bloody.BeginRepeatability();
    for (unsigned i = 0; i < 5; ++i) {
        assert(bloody.SelectedCandidateIndex() == 2 && bloody.ExpectedStep() == Step::Repeatability);
        bloody.Begin(2, Step::Repeatability, "repeat-" + std::to_string(i), 5);
        bloody.Complete(Disposition::Clean, true);
        if (i < 4) { assert(bloody.RepeatabilityPending()); bloody.BeginRepeatability(); }
    }
    assert(bloody.Complete() && bloody.RepeatabilityCompleted() == 5 && bloody.UsedActiveSeconds() == 40);
    bool sixthRejected = false;
    try { bloody.Begin(2, Step::Repeatability, "repeat-sixth", 5); } catch (const std::invalid_argument&) { sixthRejected = true; }
    assert(sixthRejected);

    TransportSequence repeatFailure(candidates, 64, 240, 5);
    repeatFailure.Begin(0, Step::CaptureInitialize, "rc"); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.Begin(0, Step::RenderInitialize, "rr"); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.Begin(0, Step::Duplex, "rd", 5); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.BeginRepeatability(); repeatFailure.Begin(0, Step::Repeatability, "rf1", 5);
    repeatFailure.Complete(Disposition::Failed, true);
    assert(!repeatFailure.Stopped() && !repeatFailure.RepeatabilityFailed() && repeatFailure.CandidateIndex() == 1);
    repeatFailure.Begin(1, Step::CaptureInitialize, "fallback"); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.Begin(1, Step::RenderInitialize, "fallback-render"); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.Begin(1, Step::Duplex, "fallback-duplex", 5); repeatFailure.Complete(Disposition::Clean, true);
    repeatFailure.BeginRepeatability();
    for (unsigned i = 0; i < 5; ++i) {
        repeatFailure.Begin(1, Step::Repeatability, "fallback-repeat-" + std::to_string(i), 5);
        repeatFailure.Complete(Disposition::Clean, true);
        if (i < 4) repeatFailure.BeginRepeatability();
    }
    assert(repeatFailure.Complete() && repeatFailure.SelectedCandidateIndex() == 1);

    TransportSequence repeatCleanupUnknown(candidates, 64, 240, 5);
    repeatCleanupUnknown.Begin(0, Step::CaptureInitialize, "rcu-c"); repeatCleanupUnknown.Complete(Disposition::Clean, true);
    repeatCleanupUnknown.Begin(0, Step::RenderInitialize, "rcu-r"); repeatCleanupUnknown.Complete(Disposition::Clean, true);
    repeatCleanupUnknown.Begin(0, Step::Duplex, "rcu-d", 5); repeatCleanupUnknown.Complete(Disposition::Clean, true);
    repeatCleanupUnknown.BeginRepeatability(); repeatCleanupUnknown.Begin(0, Step::Repeatability, "rcu-repeat", 5);
    repeatCleanupUnknown.Complete(Disposition::Failed, false);
    assert(repeatCleanupUnknown.Stopped() && repeatCleanupUnknown.CandidateIndex() == 0);

    TransportSequence flow8(candidates, 24, 40);
    flow8.Begin(0, Step::CaptureInitialize, "fc0"); flow8.Complete(Disposition::Clean, true);
    flow8.Begin(0, Step::RenderInitialize, "fr0"); flow8.Complete(Disposition::Clean, true);
    flow8.Begin(0, Step::Duplex, "fd0", 5); flow8.Complete(Disposition::Clean, true);
    assert(flow8.Stopped() && flow8.RepeatabilityPending());

    TransportSequence unknown(candidates, 24, 40);
    unknown.Begin(0, Step::CaptureInitialize, "uc0"); unknown.Complete(Disposition::Clean, true);
    unknown.Begin(0, Step::RenderInitialize, "ur0"); unknown.Complete(Disposition::Clean, true);
    unknown.Begin(0, Step::Duplex, "ud0", 5); unknown.Complete(Disposition::Unknown, true);
    assert(unknown.Stopped() && !unknown.RepeatabilityPending() && unknown.CandidateIndex() == 0);
    return 0;
}

int main() {
    try { return Run(); }
    catch (const std::exception& error) { std::cerr << "unexpected exception: " << error.what() << '\n'; return 2; }
}
