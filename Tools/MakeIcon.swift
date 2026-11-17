import AppKit

let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
let screenSizes = [16, 32, 128, 256, 512]
let colors = [
    NSColor(srgbRed: 0.14, green: 0.22, blue: 0.49, alpha: 1),
    NSColor(srgbRed: 0.31, green: 0.39, blue: 0.75, alpha: 1),
]

func makeImage(at size: Int) throws -> Data {
    let width = CGFloat(size)
    let image = NSImage(size: NSSize(width: width, height: width), flipped: false) { _ in
        let scale = width / 1024
        NSAffineTransform(transform: AffineTransform(scale: scale)).concat()
        let tile = NSBezierPath(roundedRect: NSRect(x: 108, y: 108, width: 808, height: 808), xRadius: 186, yRadius: 186)
        NSGradient(starting: colors[0], ending: colors[1])?.draw(in: tile, angle: 65)

        let frame = NSBezierPath(roundedRect: NSRect(x: 260, y: 371, width: 504, height: 279), xRadius: 104, yRadius: 104)
        NSColor.white.setStroke()
        frame.lineWidth = 31
        frame.stroke()
        let eye = NSBezierPath(roundedRect: NSRect(x: 333, y: 444, width: 351, height: 133), xRadius: 44, yRadius: 44)
        NSColor.white.withAlphaComponent(0.23).setFill()
        eye.fill()
        NSColor.white.setFill()
        NSBezierPath(ovalIn: NSRect(x: 466, y: 468, width: 86, height: 86)).fill()
        return true
    }
    guard let data = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: data),
          let png = bitmap.representation(using: .png, properties: [:]) else {
        throw CocoaError(.fileWriteUnknown)
    }
    return png
}

for size in screenSizes {
    try makeImage(at: size).write(to: output.appendingPathComponent("icon_\(size)x\(size).png"))
    try makeImage(at: size * 2).write(to: output.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
