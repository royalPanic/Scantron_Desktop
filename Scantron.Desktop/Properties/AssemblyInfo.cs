using System.Runtime.CompilerServices;

// The end-to-end hub tests drive MainViewModel's real inbound-push path rather than a stand-in,
// because a stand-in is exactly the kind of thing that can quietly stop matching production.
// A test-assembly attribute is narrower than making that path public: nothing outside the
// solution can reach it, and the view model keeps no test-only surface of its own.
[assembly: InternalsVisibleTo("Scantron.Desktop.Tests")]