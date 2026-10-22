#if os(macOS)
import AppKit

// Menu bar icon (3.1): the usage arc as a template image, so it follows the
// menu bar's light/dark and active/inactive look by itself. A template image
// carries no color, so warning and critical add a small colored dot instead of
// recoloring the arc (decision M3); that image is drawn in the label color.
enum StatusIcon {
    static let size = NSSize(width: 18, height: 18)

    static func image(fillPercent: Double, state: Display.IconState) -> NSImage {
        // Same 5 % steps as TrayIconRenderer.
        let quantized = min(max((fillPercent / 5).rounded(.toNearestOrEven) * 5, 0), 100)
        let badge: NSColor? = switch state {
        case .normal: nil
        case .warning: NSColor(srgbRed: 1.0, green: 126 / 255, blue: 91 / 255, alpha: 1)
        case .critical: NSColor(srgbRed: 235 / 255, green: 68 / 255, blue: 56 / 255, alpha: 1)
        }

        let image = NSImage(size: size, flipped: false) { rect in
            let ink = badge == nil ? NSColor.black : NSColor.labelColor
            let stroke: CGFloat = 2.2
            let ring = rect.insetBy(dx: stroke / 2 + 1.5, dy: stroke / 2 + 1.5)
            let center = NSPoint(x: ring.midX, y: ring.midY)
            let radius = ring.width / 2

            let track = NSBezierPath(ovalIn: ring)
            track.lineWidth = stroke
            ink.withAlphaComponent(0.35).setStroke()
            track.stroke()

            if quantized > 0 {
                // From twelve o'clock, clockwise.
                let arc = NSBezierPath()
                arc.appendArc(withCenter: center, radius: radius, startAngle: 90,
                              endAngle: 90 - 360 * CGFloat(quantized) / 100, clockwise: true)
                arc.lineWidth = stroke
                arc.lineCapStyle = .round
                ink.setStroke()
                arc.stroke()
            }

            if let badge {
                let dot = NSRect(x: rect.maxX - 7, y: rect.maxY - 7, width: 7, height: 7)
                NSColor.windowBackgroundColor.setFill()
                NSBezierPath(ovalIn: dot.insetBy(dx: -1, dy: -1)).fill()
                badge.setFill()
                NSBezierPath(ovalIn: dot).fill()
            }
            return true
        }
        image.isTemplate = badge == nil
        image.accessibilityDescription = "QuotaScope"
        return image
    }
}
#endif
