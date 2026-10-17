#if os(macOS)
import AppKit
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
    var sections: [LayoutSection] { RowLayout.sections(settings, usages) }

    // UsagePopupWindow.PositionAndSize, scaled to the popover: two columns
    // get the wide popover; a forced single column or a lone gauge the
    // compact one; anything else the bar width.
    var width: CGFloat {
        let sections = sections
        if RowLayout.usesTwoColumns(settings, sections) { return 340 }
        let forcedOneColumn = settings.layoutColumns.caseInsensitiveCompare("OneColumn") == .orderedSame
        let singleGaugeOnly = sections.reduce(0) { $0 + $1.rows.count } == 1
            && settings.layoutColumns.caseInsensitiveCompare("TwoColumns") != .orderedSame
            && sections.allSatisfy { section in
                section.rows.allSatisfy { RowShapes.resolve(settings, section.usage.providerId, $0) == RowShapes.circle }
            }
        return forcedOneColumn || singleGaugeOnly ? 190 : 300
    }
}

struct PopoverView: View {
    @ObservedObject var model: UsageModel

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
        .frame(width: model.width)
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
            // Which cards share a line is RowLayout's (fixtures/layout).
            let twoColumn = RowLayout.usesTwoColumns(model.settings, sections)
            VStack(alignment: .leading, spacing: 8) {
                ForEach(Array(sections.enumerated()), id: \.offset) { index, section in
                    if index > 0 { Divider().padding(.vertical, 2) }
                    sectionHeader(section.usage)
                    let providerId = section.usage.providerId
                    let lines = RowLayout.lines(model.settings, providerId, section.rows, twoColumn: twoColumn)
                    ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                        lineView(providerId, line, now: now)
                    }
                }
            }
        }
    }

    // A bar row, or one or two cards of one shape.
    @ViewBuilder
    private func lineView(_ providerId: String, _ line: LayoutLine, now: Date) -> some View {
        if line.shape == RowShapes.bars {
            BarRow(providerId: providerId, row: line.left, model: model, now: now)
        } else {
            HStack(alignment: .top, spacing: line.shape == RowShapes.fill ? 8 : 10) {
                card(providerId, line.shape, line.left, now: now)
                if let right = line.right { card(providerId, line.shape, right, now: now) }
            }
        }
    }

    @ViewBuilder
    private func card(_ providerId: String, _ shape: String, _ row: UsageRow, now: Date) -> some View {
        if shape == RowShapes.fill {
            FillCard(providerId: providerId, row: row, model: model, now: now)
        } else {
            GaugeCard(providerId: providerId, row: row, model: model, now: now)
        }
    }

    private func sectionHeader(_ usage: ProviderUsage) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(usage.displayName)
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(UsageBar.accent)
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

    // The user's accent color resolved to fixed sRGB: Color.accentColor also
    // turns gray in a window that is not key.
    static var accent: Color {
        Color(nsColor: NSColor.controlAccentColor.usingColorSpace(.sRGB) ?? .systemBlue)
    }

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
                         color: RowText.accent(row, settings))
            }
            footer
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 9).fill(Color.primary.opacity(0.05)))
    }

    private var percentText: String { RowText.percent(row, model.settings) }

    private var footer: some View {
        RowFooter(row: row, model: model, now: now)
    }
}

// The texts and colors every row shape shares.
enum RowText {
    static func percent(_ row: UsageRow, _ settings: AppSettings) -> String {
        guard let window = row.window else { return row.detailText ?? "--" }
        return Display.percentText(row, Display.percent(window.usedPercent, settings))
    }

    // PopupPalette.AccentFor: the band follows usage, whatever the gauge shows.
    static func accent(_ row: UsageRow, _ settings: AppSettings) -> Color {
        let used = Double(Display.roundedUsed(row.window?.usedPercent ?? 0))
        if used >= UsageAlerts.criticalAtUsed(settings) { return Color(red: 235 / 255, green: 68 / 255, blue: 56 / 255) }
        if used >= UsageAlerts.warningAtUsed(settings) { return Color(red: 1, green: 126 / 255, blue: 91 / 255) }
        return UsageBar.accent
    }
}

// Reset time (or the credits detail); clicking it toggles clock time and
// remaining time.
struct RowFooter: View {
    let row: UsageRow
    @ObservedObject var model: UsageModel
    let now: Date

    var body: some View {
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
                label.contentShape(Rectangle()).onTapGesture(perform: model.onToggleTimeMode)
            }
        }
    }
}

// Gauges (BentoCircles): label, a ring with the percent inside, the reset time.
struct GaugeCard: View {
    let providerId: String
    let row: UsageRow
    @ObservedObject var model: UsageModel
    let now: Date

    var body: some View {
        let label = model.loc.rowLabel(providerId, row.label, scope: row.scope)
        VStack(spacing: 8) {
            Text(label)
                .font(.system(size: 12, weight: .bold))
                .lineLimit(1)
                .truncationMode(.middle)
                .help(label)
                .frame(maxWidth: .infinity, alignment: .leading)
            Spacer(minLength: 0)
            if let window = row.window {
                let display = Display.percent(window.usedPercent, model.settings)
                ZStack {
                    Circle().stroke(Color.primary.opacity(0.12), lineWidth: 9)
                    Circle()
                        .trim(from: 0, to: CGFloat(min(max(display, 0), 100)) / 100)
                        .stroke(RowText.accent(row, model.settings), style: StrokeStyle(lineWidth: 9, lineCap: .round))
                        .rotationEffect(.degrees(-90))
                    Text(RowText.percent(row, model.settings))
                        .font(.system(size: 20, weight: .bold))
                        .monospacedDigit()
                }
                .frame(width: 96, height: 96)
                RowFooter(row: row, model: model, now: now)
            } else {
                Text(row.detailText ?? "--")
                    .font(.system(size: 20, weight: .bold))
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity)
        .frame(height: 176)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.05)))
    }
}

// Fill: the bar row without its bar; the card's own background fills up to
// the displayed percent, with a stronger edge where it ends so the value reads
// without a bar. Text stays on a tint, never on solid accent.
struct FillCard: View {
    let providerId: String
    let row: UsageRow
    @ObservedObject var model: UsageModel
    let now: Date

    private static let radius: CGFloat = 9
    private static let edgeWidth: CGFloat = 2

    var body: some View {
        let label = model.loc.rowLabel(providerId, row.label, scope: row.scope)
        VStack(alignment: .trailing, spacing: 2) {
            HStack(alignment: .firstTextBaseline) {
                Text(label)
                    .font(.system(size: 13, weight: .bold))
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(label)
                Spacer(minLength: 8)
                Text(RowText.percent(row, model.settings))
                    .font(.system(size: 14, weight: .bold))
                    .monospacedDigit()
            }
            RowFooter(row: row, model: model, now: now)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 7)
        .frame(maxWidth: .infinity)
        .background(fill)
        .background(RoundedRectangle(cornerRadius: Self.radius).fill(Color.primary.opacity(0.05)))
        .clipShape(RoundedRectangle(cornerRadius: Self.radius))
    }

    @ViewBuilder
    private var fill: some View {
        if let window = row.window {
            let display = CGFloat(min(max(Display.percent(window.usedPercent, model.settings), 0), 100)) / 100
            let color = RowText.accent(row, model.settings)
            GeometryReader { geometry in
                let width = geometry.size.width * display
                // A value narrower than the edge is drawn as all edge; at 100%
                // the card's own border is where the fill ends.
                let edge = display >= 1 ? 0 : min(width, Self.edgeWidth)
                HStack(spacing: 0) {
                    Rectangle().fill(color.opacity(0.24)).frame(width: width - edge)
                    Rectangle().fill(color.opacity(0.85)).frame(width: edge)
                    Spacer(minLength: 0)
                }
            }
        }
    }
}
#endif
