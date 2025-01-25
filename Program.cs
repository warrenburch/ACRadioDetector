using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

class Program
{
    static async Task Main(string[] args)
    {
        double latitude = 48.7081; // Latitude of KORS
        double longitude = -122.9101; // Longitude of KORS
        double radiusNm = 25; // Radius in nautical miles        
        var tailNumbers = new List<string> { "*", "N821G", "N2241N", "N459CD" }; // Tail numbers to track


        while (true)
        {

            // Query OpenSky API
            var airplanes = await GetNearbyAircraftFromOpenSky(latitude, longitude, radiusNm);

            // use FlightAware API (too expensive, was supposed to be free for feeders???)
            //var airplanes = await GetNearbyAircraftFromFlightAware("KORS", korsLatitude, korsLongitude, radiusNm);

            // Filter airplanes by tail number (wildcard matching)
            var matchingPlanes = airplanes.Where(plane => MatchesWildcard(plane.TailNumber, tailNumbers)).ToList();
            
            // Output results
            if (matchingPlanes.Any())
            {
                foreach (var plane in matchingPlanes)
                {
                    // show time and aircraft details
                    Console.WriteLine($"\n Time:{ DateTime.Now }  Tail: {plane.TailNumber}, Altitude: {plane.Altitude}, Distance: {plane.DistanceNm:F2} NM");
                }
            }
            else
            {
                Console.Write($".");
            }

            Thread.Sleep(1000 * 60 * 3);
        }
    }

    static async Task<List<Aircraft>> GetNearbyAircraftFromOpenSky(double lat, double lon, double radiusNm)
    {
        string url = "https://opensky-network.org/api/states/all";
        using var httpClient = new HttpClient();

        var byteArray = System.Text.Encoding.ASCII.GetBytes($"warrenburch:*5@y5Q#@ZU");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

        try
        {
            var response = await httpClient.GetStringAsync(url);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var apiResponse = JsonSerializer.Deserialize<ApiResponse>(response, options);

            // Parse and calculate distances
            var aircraftList = apiResponse.States
                .Where(state => state[5] != null && state[6] != null) // Ensure lat/lon are not null
                .Select(state => new Aircraft
                {
                    TailNumber = state[1]?.ToString() ?? "Unknown", // Tail number (callsign)
                    Latitude = Convert.ToDouble(state[6].ToString()),
                    Longitude = Convert.ToDouble(state[5].ToString()),
                    Altitude = state[13] != null ? Convert.ToDouble(state[13].ToString()) : 0, // Altitude
                    DistanceNm = CalculateDistance(lat, lon, Convert.ToDouble(state[6].ToString()), Convert.ToDouble(state[5].ToString()))
                })
                .ToList();

            return aircraftList.Where(a => a.DistanceNm <= radiusNm).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error querying OpenSky: {ex.Message}");
            return new List<Aircraft>();
        }
    }


    /// using AeroAPI https://www.flightaware.com/aeroapi/portal <summary>
    /// ******************* WAY TOO EXPENSIVE ***************************
    /// </summary>
    /// <param name="airportCode"></param>
    /// <param name="lat"></param>
    /// <param name="lon"></param>
    /// <param name="radiusNm"></param>
    /// <returns></returns>
    static async Task<List<Aircraft>> GetNearbyAircraftFromFlightAware(string airportCode, double lat, double lon, double radiusNm)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("x-apikey", "x7y5sZ8SuztzYUbPgoG7EprO7vKSxlF2");

        // Construct the request URL
        //string url = $"https://aeroapi.flightaware.com/aeroapi/airports/{airportCode}/flights?radius={radiusNm}";
        string url = $"https://aeroapi.flightaware.com/aeroapi/flights/search/advanced?query=%7Borig_or_dest+%7B{airportCode}%7D%7D";

        string json;

        // read from cache for debugging
        //if (File.Exists("cache.txt"))
        //{
        //    json = File.ReadAllText("cache.txt");         
        //}
        //else
        {
            // Send the GET request
            var response = await client.GetAsync(url);

            // Check if the response indicates an authentication error
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Console.WriteLine("Invalid API key.");
                return new List<Aircraft>();
            }

            response.EnsureSuccessStatusCode();

            // Parse the JSON response
            json = await response.Content.ReadAsStringAsync();
            File.WriteAllText("cache.txt", json);
        }

        
        using var doc = JsonDocument.Parse(json);

        // Extract and return the aircraft list
        var aircraft = doc.RootElement.GetProperty("flights");
        var aircraftList = new List<Aircraft>(aircraft.GetArrayLength());

        for (int i = 0; i < aircraft.GetArrayLength(); i++)
        {
            // List all properties available in the current aircraft JSON object
            //foreach (var property in aircraft[i].EnumerateObject())
            //{
            //    Console.WriteLine($"Property Name: {property.Name}, Value: {property.Value}");
            //}
            var acDetails = aircraft[i];
            var lastPos = acDetails.GetProperty("last_position");
            var latitude = 0.0;
            var longitude = 0.0;
            var altitude = 0.0;
            var distanceNm = 0.0;
            if (lastPos.ValueKind != JsonValueKind.Null)
            {
                latitude = lastPos.GetProperty("latitude").GetDouble();
                longitude = lastPos.GetProperty("longitude").GetDouble();
                altitude = lastPos.GetProperty("altitude").GetDouble();
                distanceNm = CalculateDistance(lat, lon, latitude, longitude);
            }
            
            var ac = new Aircraft
            {
                TailNumber = acDetails.GetProperty("ident").GetString() ?? "Unknown",
                Longitude = longitude,
                Latitude = latitude,
                Altitude = altitude,
                DistanceNm = distanceNm
            };
            aircraftList.Add(ac);
        }

        return aircraftList;
    }

    static bool MatchesWildcard(string input, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            // Convert wildcard to regex: * -> .* and ? -> .
            string regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    static double CalculateDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3440.065; // Radius of the Earth in nautical miles
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;

    // Classes for JSON parsing
    class ApiResponse
    {
        public List<List<object>> States { get; set; }
    }

    class Aircraft
    {
        public string TailNumber { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Altitude { get; set; }
        public double DistanceNm { get; set; }
    }
}
