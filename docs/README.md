# Hearbud Documentation

Welcome to the Hearbud documentation hub. This directory contains comprehensive documentation for understanding, developing, and troubleshooting Hearbud.

## 📚 Documentation Index

### For Newcomers
- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Start here! Comprehensive overview of the application architecture, component design, data flow, and key design decisions.
- **[WORKING_WITH_AUDIO.md](WORKING_WITH_AUDIO.md)** - Deep dive into audio concepts, WASAPI details, DSP math, and audio signal processing in Hearbud.

### For Developers
- **[CONTRIBUTING.md](CONTRIBUTING.md)** - Development setup, code conventions, testing guidelines, debugging tips, and release process.

### For Users and Maintainers
- **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** - Common issues, error messages, and solutions for both users and developers.

## Suggested Reading Order

### For Developers Wanting to Modify Audio Pipeline
1. [ARCHITECTURE.md](ARCHITECTURE.md) - Understand the high-level architecture
2. [WORKING_WITH_AUDIO.md](WORKING_WITH_AUDIO.md) - Learn audio/DSP specifics
3. [CONTRIBUTING.md](CONTRIBUTING.md) - Review coding conventions and testing

### For Users Experiencing Issues
1. Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Find your problem and solution
2. If not listed, check [ARCHITECTURE.md](ARCHITECTURE.md) for context
3. Report an issue on GitHub with logs and details

### For Contributors/LLMs Getting Up to Speed
1. [ARCHITECTURE.md](ARCHITECTURE.md) - Complete system overview
2. [WORKING_WITH_AUDIO.md](WORKING_WITH_AUDIO.md) - Audio/DSP math
3. [CONTRIBUTING.md](CONTRIBUTING.md) - Development workflow
4. [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common pitfalls

## Key Concepts to Understand

### Architecture
- **Three Parallel WAV Files:** System, mic, and mix are always written separately
- **Loopback as Clock Source:** System audio drives timing; mic is buffered and synchronized
- **Async I/O with Pooling:** Disk writes offloaded to background thread, buffer pooling prevents GC pressure

### Audio/DSP
- **WASAPI Loopback Capture:** Captures what Windows plays to a speaker endpoint
- **Soft Clipping:** Tanh-based limiting prevents harsh distortion
- **TPDF Dither:** Reduces quantization noise when converting to 16-bit
- **Ring Buffer Synchronization:** Handles clock drift between mic/loopback

### Design Patterns
- **Producer-Consumer:** Audio callbacks produce, background writer thread consumes
- **Idempotent Dispose:** Safe to call multiple times
- **Throttle-Decouple:** Audio callbacks (~100 Hz) decoupled from UI updates (10 Hz)

## Quick Links

- **[Project README](../README.md)** - Project overview, features, and getting started
- **[GitHub Repository](https://github.com/radekstepan/hearbud)** - Source code and issue tracker
- **[WASAPI Documentation](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)** - Windows Audio Session API
- **[CSCore Library](https://github.com/filoe/cscore)** - Audio processing library used by Hearbud

## Document Structure

Each documentation file serves a specific purpose:

| File | Audience | Purpose |
|------|----------|---------|
| ARCHITECTURE.md | Devs, LLMs, curious users | System architecture, data flow, design decisions, performance |
| WORKING_WITH_AUDIO.md | Devs, LLMs | Audio/DSP theory, WASAPI specifics, math, sync, metering, dithering |
| CONTRIBUTING.md | Devs, contributors | Build setup, coding style, testing, releasing |
| TROUBLESHOOTING.md | Users, devs | Common problems, error meanings, solutions, log analysis |

## Conventions Used in Documentation

- **Code blocks:** Uses appropriate syntax highlighting (C#, PowerShell)
- **Mermaid diagrams:** Visual architecture and flow (if viewer supports)
- **Tables:** Quick reference for parameters, errors, formulas
- **Warning/Note/Tip callouts:** Highlight important information
- **Cross-references:** Links between documents for deep dives

## Getting Help

- **User:** Check [TROUBLESHOOTING.md](TROUBLESHOOTING.md) first, then post issue on GitHub
- **Developer:** See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution process
- **Question not answered:** Open an issue on [GitHub](https://github.com/radekstepan/hearbud/issues)

---

**Last Updated:** December 23, 2025

**Hearbud Version:** 0.2.7
