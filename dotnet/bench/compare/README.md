# Rails vs .NET, side by side

Measures the Rails app and the .NET port on the same machine, the same data and the same requests,
following the repo's `bench/compare_http.rb`:

- each app in its own container pinned to `APP_CPUS` (default `8-11`), the load generator on
  `CLIENT_CPUS` (`12-15`); pick sets that don't share physical cores (`lscpu -e`);
- 16 keep-alive clients, uncompressed responses read in full, every response must be 200;
- a fresh copy of one seed per run, built by the Rails app itself (`seed.rb`): the test fixtures'
  people and rooms, a 120-message "busy" series in the watercooler room with mentions, links and
  boosts, and conversation in other rooms;
- the workloads of the top-level README's table: the room page, a messages page
  (`before=busy_060`), the sidebar, search (`q=coffee`) and posting a message;
- configurations alternated across rounds; medians reported.

Configurations: `dotnet`, `rails-1` (Puma with one worker, as `compare_http.rb` runs it) and
`rails-4` (one worker per allocated thread — Ruby's GVL keeps one worker to about one core).

```sh
dotnet/bench/compare/compare.sh                      # 2 rounds × 3 configurations, ~15 minutes
ROUNDS=4 DURATION=10 ./compare.sh                    # more rounds
CLIENT=ruby ./compare.sh                             # the repo's Ruby client (bench/http_client.rb)
CONFIGS="dotnet rails-4" REBUILD=1 ./compare.sh      # rebuild both images first
RESEED=1 ./compare.sh                                # regenerate the seed
```

Needs Docker, the .NET SDK and Python 3. Images are built on first use: `campfire-rails:bench` from
the committed Rails app (`git archive HEAD`) and `campfire-dotnet:bench` from the working tree.
Everything else lives in `.work/` (ignored).

## Clients

The default client is `bench/Campfire.Bench`, compiled .NET. The repo's Ruby client saturates
before the .NET server does — it measured ~1,200 req/s on the room page where the server sustains
several times that — so its numbers for .NET are the client's ceiling, not the server's. Rails stays
well below either client's limit. `CLIENT=ruby` is there to reproduce the repo's method exactly.

## Reading the results

Ratios to Rails are comparable with the top-level README's table only roughly: that table used
another machine and its own seed. `summarize.py` prints both side by side.
