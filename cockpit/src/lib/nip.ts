// Polish NIP checksum weights applied to digits d1..d9.
const NIP_WEIGHTS = [6, 5, 7, 2, 3, 4, 5, 6, 7] as const

// Generates a checksum-valid 10-digit Polish NIP. Nine random digits, then the
// computed check digit. A modulo of 10 is invalid per the algorithm, so retry.
export function randomValidNip(): string {
  for (;;) {
    const digits = Array.from({ length: 9 }, () => Math.floor(Math.random() * 10))
    const sum = digits.reduce((acc, d, i) => acc + d * NIP_WEIGHTS[i], 0)
    const check = sum % 11
    if (check === 10) continue
    return digits.join('') + check
  }
}
