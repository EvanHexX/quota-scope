#if os(macOS)
import SwiftUI
import QuotaCore

// State the popover draws. The controller owns it and updates it on the main
// thread; the view only reads it and reports clicks back.
final class UsageModel: ObservableObject {
    @Published var usages: [ProviderUsage] = []
    @Published var settings = AppSettings()
    @Published var loc = Loc.english
    @Published var isPinned = false
    @Published var refreshing = false
    @Published var lastUpdate: Date?

    var onRefresh: () -> Void = {}
    var onTogglePin: () -> Void = {}
    var onToggleTimeMode: () -> Void = {}
    var onOpenSettings: () -> Void = {}
    var onSignIn: () -> Void = {}

    // Visible rows per provider in the user's order, as the Windows popup.
    var sections: [(usage: ProviderUsage, rows: [UsageRow])] {
        usages.map { usage in
            let visible = usage.rows.filter { RowShapes.isVisible(settings, usage.providerId, $0) }
            return (usage, RowShapes.order(settings, usage.providerId, visible) { $0.label })
        }
    }
}

struct PopoverView: View {
    @ObservedObject var model: UsageModel

    static let width: CGFloat = 300

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            Divider().padding(.bottom, 8)
            // Remaining time ticks every minute while the popover is open.
            TimelineView(.everyMinute) { context in
                content(now: context.date)
            }
        }
        .padding(12)
        .frame(width: Self.width)
        .contextMenu { contextMenu }
    }

    private var header: some View {
        HStack(spacing: 6) {
            Text(model.loc.t("Usage", "사용량"))
                .font(.headline)
            if let lastUpdate = model.lastUpdate {
                Text(lastUpdate, style: .time)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .monospacedDigit()
            }
            Spacer()
            Button(action: model.onRefresh) {
                Image(systemName: "arrow.clockwise")
                    .rotationEffect(.degrees(model.refreshing ? 360 : 0))
                    .animation(model.refreshing ? .linear(duration: 1).repeatForever(autoreverses: false) : .default,
                               value: model.refreshing)
            }
            .buttonStyle(.borderless)
            .help(model.loc.t("Refresh", "새로 고침"))
            Button(action: model.onTogglePin) {
                Image(systemName: model.isPinned ? "pin.fill" : "pin")
            }
            .buttonStyle(.borderless)
            .help(model.isPinned ? model.loc.t("Unpin", "고정 해제") : model.loc.t("Pin", "고정"))
            Button(action: model.onOpenSettings) {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.borderless)
            .help(model.loc.t("Settings", "설정"))
        }
        .padding(.bottom, 8)
    }

    @ViewBuilder
    private func content(now: Date) -> some View {
        let sections = model.sections
        if sections.isEmpty {
            Text(model.loc.t("Waiting for connection", "연결 대기 중"))
                .font(.callout)
                .foregroundStyle(.secondary)
                .padding(.vertical, 12)
        } else {
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Array(sections.enumerated()), id: \.offset) { index, section in
                    if index > 0 { Divider().padding(.vertical, 2) }
                    sectionHeader(section.usage)
                    ForEach(Array(section.rows.enumerated()), id: \.offset) { _, row in
                        BarRow(providerId: section.usage.providerId, row: row, model: model, now: now)
                    }
                }
            }
        }
    }

    private func sectionHeader(_ usage: ProviderUsage) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(usage.displayName)
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(Color.accentColor)
            Spacer(minLength: 12)
            Text(usage.statusText)
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
                .truncationMode(.tail)
                .help(usage.statusText)
                .opacity(usage.state == .stale ? 0.75 : 1)
            // Claude signed out: one click opens Terminal running `claude
            // /login`, the same as Reconnect in the menu.
            if usage.providerId == "claude", usage.state == .unauthenticated {
                Button(model.loc.t("Sign in", "로그인"), action: model.onSignIn)
                    .controlSize(.small)
            }
        }
    }

    @ViewBuilder
    private var contextMenu: some View {
        Button(model.loc.t("Refresh", "새로 고침"), action: model.onRefresh)
        Button(model.isPinned ? model.loc.t("Unpin", "고정 해제") : model.loc.t("Pin", "고정"), action: model.onTogglePin)
        Button(model.loc.t("Settings...", "설정..."), action: model.onOpenSettings)
    }
}

// Drawn by hand rather than with ProgressView: a system progress bar turns
// gray whenever its window is not key, and the popover and the pinned panel
// almost never are.
struct UsageBar: View {
    let percent: Int
    let color: Color

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.12))
                Capsule()
                    .fill(color)
                    .frame(width: geometry.size.width * CGFloat(min(max(percent, 0), 100)) / 100)
            }
        }
        .frame(height: 6)
    }
}

// Bars, the default row shape (decision M10): label, reset time, percent, and
// a capsule bar colored by the same usage bands as the Windows popup.
struct BarRow: View {
    let providerId: String
    let row: UsageRow
    @ObservedObject var model: UsageModel
    let now: Date

    var body: some View {
        let settings = model.settings
        let loc = model.loc
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(loc.rowLabel(providerId, row.label, scope: row.scope))
                    .font(.system(size: 13, weight: .semibold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(loc.rowLabel(providerId, row.label, scope: row.scope))
                Spacer(minLength: 8)
                Text(percentText)
                    .font(.system(size: 12, weight: .semibold))
                    .monospacedDigit()
            }
            if let window = row.window {
                UsageBar(percent: Display.percent(window.usedPercent, settings),
                         color: accent(Display.roundedUsed(window.usedPercent)))
            }
            footer
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 9).fill(Color.primary.opacity(0.05)))
    }

    private var percentText: String {
        guard let window = row.window else { return row.detailText ?? "--" }
        return Display.percentText(row, Display.percent(window.usedPercent, model.settings))
    }

    @ViewBuilder
    private var footer: some View {
        let text = row.window == nil ? "" : Display.rowFooter(row, model.settings, model.loc, now: now)
        if !text.isEmpty {
            let label = Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .monospacedDigit()
                .lineLimit(1)
            if RowShapes.isCreditsRow(row.label) {
                label
            } else {
                // Clicking the time toggles clock time and remaining time.
                label.contentShape(Rectangle()).onTapGesture(perform: model.onToggleTimeMode)
            }
        }
    }

    // PopupPalette.AccentFor: the band follows usage, whatever the bar shows.
    private func accent(_ used: Int) -> Color {
        if Double(used) >= UsageAlerts.criticalAtUsed(model.settings) { return Color(red: 235 / 255, green: 68 / 255, blue: 56 / 255) }
        if Double(used) >= UsageAlerts.warningAtUsed(model.settings) { return Color(red: 1, green: 126 / 255, blue: 91 / 255) }
        return .accentColor
    }
}
#endif
