# The repo's own load client (bench/http_client.rb, mounted read-only at /bench), measuring the
# workloads compare.sh measures: the four bench/compare_http.rb paths plus posting a message.
#   ruby driver.rb BASE_URL LABELS_JSON DURATION CONCURRENCY OUTPUT_JSON
require "json"
require "/bench/http_client"

class BenchmarkHTTPClient
  # As upstream, except that a 303 also counts as signed in: the .NET port answers non-GET
  # requests with 303 See Other, so fetch never replays a DELETE or PATCH on redirect.
  def login(labels)
    cookies = {}
    connection.start do |http|
      response = http.get("/session/new", "Accept-Encoding" => "identity")
      raise "sign-in page: HTTP #{response.code}" unless response.code == "200"
      merge_cookies(cookies, response)
      token = response.body[/<meta name="csrf-token" content="([^"]*)"/, 1]
      raise "sign-in page has no CSRF token" unless token
      request = Net::HTTP::Post.new("/session")
      request["Cookie"] = cookie_header(cookies)
      request["Origin"] = @base.to_s
      request["Sec-Fetch-Site"] = "same-origin"
      request.set_form_data(email_address: labels.fetch("emails.david"), password: labels.fetch("passwords.all"),
        authenticity_token: CGI.unescapeHTML(token))
      response = http.request(request)
      merge_cookies(cookies, response)
      raise "login failed: HTTP #{response.code}" unless %w[ 302 303 ].include?(response.code) && cookies.key?("session_token")
    end
    cookie_header(cookies)
  end

  # POST /rooms/:id/messages as the composer does (a Turbo Stream response), a fresh body each time.
  def measure_post(room_path, cookie, concurrency:, duration:)
    token = connection.start do |http|
      page = http.get(room_path, "Cookie" => cookie, "Accept-Encoding" => "identity")
      CGI.unescapeHTML(page.body[/<meta name="csrf-token" content="([^"]*)"/, 1])
    end
    counter = 0
    lock = Mutex.new
    start = clock
    deadline = start + duration
    workers = Array.new(concurrency) do
      Thread.new do
        result = { latencies: [], statuses: Hash.new(0), errors: 0 }
        connection.start do |http|
          while clock < deadline
            number = lock.synchronize { counter += 1 }
            request = Net::HTTP::Post.new("#{room_path}/messages")
            request["Cookie"] = cookie
            request["X-CSRF-Token"] = token
            request["Accept"] = "text/vnd.turbo-stream.html, text/html, application/xhtml+xml"
            request["Accept-Encoding"] = "identity"
            request["Origin"] = @base.to_s
            request["Sec-Fetch-Site"] = "same-origin"
            request.set_form_data("message[body]" => "<div>Benchmark message #{number} about coffee</div>",
              "message[client_message_id]" => "bench-#{Process.pid}-#{number}")
            requested = clock
            response = http.request(request)
            result[:latencies] << (clock - requested) * 1000
            result[:statuses][response.code] += 1
          end
        end
        result
      rescue IOError, SystemCallError, Timeout::Error, SocketError, Net::HTTPBadResponse
        result[:errors] += 1
        result
      end
    end
    samples = workers.map(&:value)
    elapsed = clock - start
    latencies = samples.flat_map { _1[:latencies] }.sort
    statuses = Hash.new(0)
    samples.each { |sample| sample[:statuses].each { |status, count| statuses[status] += count } }
    errors = samples.sum { _1[:errors] }
    raise "post: HTTP statuses #{statuses}, #{errors} transport errors" unless errors.zero? && statuses.keys == [ "200" ]
    { rps: latencies.size / elapsed, latency_ms: { p50: percentile(latencies, 0.50), p99: percentile(latencies, 0.99) } }
  end
end

base, labels_path, duration, concurrency, output = ARGV
labels = JSON.parse(File.read(labels_path))
duration = Float(duration)
concurrency = Integer(concurrency)
room = "/rooms/#{labels.fetch('rooms.watercooler')}"
paths = {
  "room" => room,
  "messages" => "#{room}/messages?before=#{labels.fetch('messages.busy_060')}",
  "sidebar" => "/users/me/sidebar",
  "search" => "/searches?q=coffee"
}

client = BenchmarkHTTPClient.new(base)
cookie = client.login(labels)
results = {}
paths.each do |name, path|
  client.measure(path, cookie, concurrency: 1, duration: 3) # warmup, as compare_http.rb does
  results[name] = client.measure(path, cookie, concurrency: concurrency, duration: duration)
end
client.measure_post(room, cookie, concurrency: 1, duration: 3)
results["post"] = client.measure_post(room, cookie, concurrency: concurrency, duration: duration)

unified = results.transform_values { |r| { rps: r[:rps], p50: r[:latency_ms][:p50], p99: r[:latency_ms][:p99] } }
File.write(output, JSON.pretty_generate(unified))
unified.each { |name, r| puts format("| %-9s | %9.0f | %7.2f | %7.2f |", name, r[:rps], r[:p50], r[:p99]) }
