# Builds the benchmark seed with the Rails app itself (so the schema bookkeeping Rails needs is
# present), mirroring the names bench/compare_http.rb's labels.json refers to: the test fixtures'
# people and rooms, plus a "busy" series of messages in the watercooler room.
ActiveJob::Base.queue_adapter = :inline # no Redis while seeding; pushes find no subscriptions

password = "secret123456"
now = Time.current.change(usec: 0)

account = Account.create!(name: "37signals")

people = {
  david:  User.create!(name: "David",  email_address: "david@37signals.com",  password: password, role: :administrator),
  jason:  User.create!(name: "Jason",  email_address: "jason@37signals.com",  password: password, role: :administrator),
  jz:     User.create!(name: "JZ",     email_address: "jz@37signals.com",     password: password, bio: "Designer"),
  kevin:  User.create!(name: "Kevin",  email_address: "kevin@37signals.com",  password: password, bio: "Programmer")
}
Current.user = people[:david] # rooms default their creator to the current user
extras = 24.times.map { |n| User.create!(name: "Person #{n + 1}", email_address: "person#{n + 1}@example.com", password: password) }
bender = User.create_bot!(name: "Bender Bot")

def room(type, name, creator, members)
  type.create_for({ name: name, creator: creator }, users: members)
end

everyone = people.values + extras
rooms = {
  pets:        room(Rooms::Open,   "All Pets",  people[:david], everyone),
  hq:          room(Rooms::Open,   "HQ",        people[:david], everyone),
  watercooler: room(Rooms::Closed, "All Talk",  people[:david], people.values + [ bender ]),
  designers:   room(Rooms::Closed, "Designers", people[:david], people.values)
}
directs = [ [ :jason ], [ :kevin ], [ :jz ] ].map { |others| Rooms::Direct.find_or_create_for(User.where(id: [ people[:david].id ] + others.map { people[_1].id })) }

texts = [
  "Morning! Anyone up for coffee before standup?",
  "Shipped the new release, see https://example.com/releases/42 for notes",
  "Reviewing the design for the sidebar today",
  "Lunch plans? I'm thinking tacos",
  "The deploy finished green 🎉",
  "Can someone pair on the flaky test this afternoon?",
  "Coffee machine on the 3rd floor is fixed ☕",
  "Notes from the retro are in the doc",
  "Heads up: maintenance window tonight at 10pm",
  "That bug was a timezone issue all along"
]

labels = {
  "rooms.watercooler" => rooms[:watercooler].id,
  "emails.david" => people[:david].email_address,
  "passwords.all" => password
}

speakers = people.values
120.times do |index|
  speaker = speakers[index % speakers.size]
  body = texts[index % texts.size]
  body = "#{ActionText::Attachment.from_attachable(people[:jason]).to_html} #{body}" if (index % 9).zero?
  message = rooms[:watercooler].messages.create!(body: "<div>#{body}</div>", creator: speaker, created_at: now - (120 - index).minutes)
  [ people[:david], people[:jz] ].first((index % 3)).each { |booster| message.boosts.create!(content: %w[👍 🎉 ❤️][index % 3], booster: booster) }
  labels["messages.busy_%03d" % (index + 1)] = message.id
end

# Some conversation elsewhere, so search spans rooms and the sidebar has unread rooms
[ rooms[:pets], rooms[:hq], rooms[:designers], *directs ].each_with_index do |other, offset|
  30.times do |index|
    other.messages.create!(body: "<div>#{texts[(index + offset) % texts.size]}</div>", creator: speakers[(index + offset) % speakers.size],
      created_at: now - (240 - index).minutes)
  end
end
Membership.where(user: people[:david], room: [ rooms[:pets], rooms[:designers] ]).update_all(unread_at: now)

File.write("/seed/labels.json", JSON.pretty_generate(labels.slice("rooms.watercooler", "emails.david", "passwords.all", "messages.busy_060")))
ActiveRecord::Base.connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
puts "Seeded: #{User.count} users, #{Room.count} rooms, #{Message.count} messages, #{Boost.count} boosts"
