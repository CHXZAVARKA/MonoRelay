struct AdaptiveQuality {
    static let id = "auto"
    private(set) var index = 1
    private var warmup = 3
    private var strained = 0
    private var stable = 0
    private var mayRaise = true

    mutating func beginStream() {
        warmup = 3
        strained = 0
        stable = 0
    }

    // The transport can send no frames for an unchanged image. Count actual skips
    // as overload, and require sustained active video before trying a higher tier.
    mutating func sample(rendered: Int, skipped: Int, cap: Int, lastIndex: Int) -> Bool {
        if warmup > 0 {
            warmup -= 1
            return false
        }
        strained = skipped >= 3 && skipped * 5 >= rendered + skipped ? strained + 1 : 0
        stable = mayRaise && skipped == 0 && rendered * 4 >= cap * 3 ? stable + 1 : 0
        if index > 0 && strained >= 4 {
            index -= 1
        } else if index < lastIndex && stable >= 45 {
            index += 1
        } else {
            return false
        }
        mayRaise = false
        beginStream()
        return true
    }
}
