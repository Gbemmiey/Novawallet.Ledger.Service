// Collections share one API host, database and process-wide environment variables, and the
// rate-limit tests depend on exact request counts, so collections must not run side by side.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
