// Lifetime and retention shared by binding requests and key requests.
export const requestLifetimeTicks = 864000000000n; // 24 h
export const retentionTicks = 30n * requestLifetimeTicks; // 30 days
export const prunedRowsPerCreate = 100;
