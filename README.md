# Reaper-DOS

A concurrent HTTP load testing tool for performance benchmarking.

⚠️ WARNING: Only test servers you own or have explicit permission to test.  
Using this tool against other servers is illegal and considered a denial-of-service attack.

## Usage

1. Enter the URL of your **own test server**.
2. Select mode (`baseline`, `spike`, `soak`), concurrency, and duration.
3. Results are logged to a CSV file in the program folder.

## Modes

- **baseline**:  ramp-up to a request rate.
- **spike**: sudden bursts of high request volume.
- **soak** for long-duration testing.

## Disclaimer

This tool is **strictly for authorized load testing**. Misuse may result in criminal charges.

# Story
This project started as a small experiment while I was learning about
HTTP networking, concurrency, and how web servers handle large amounts
of traffic.

I wanted to understand things like:
- how concurrent requests affect server performance
- how latency changes under heavy load
- how to log and analyze request results

So I built a simple load testing tool in C# that can simulate different
traffic patterns (baseline, spike, and soak tests) and record results
to a CSV file for analysis.

During development I learned a lot about:
- asynchronous programming
- connection pooling
- performance monitoring
- handling large amounts of network requests efficiently

⚠️ Important:  
This project is intended **only for educational purposes and for testing
servers that you own or have explicit permission to test**. Running load
tests against systems without permission may be illegal.

The goal of this repository is to demonstrate how load testing tools
work and to provide a simple environment for experimenting with network
performance and benchmarking.
