package com.tello4quest2;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.wifi.WifiNetworkSpecifier;
import android.os.PatternMatcher;

import com.unity3d.player.UnityPlayer;

/**
 * Connects the device to a Wi-Fi network whose SSID starts with a given
 * prefix (the Tello's own hotspots are always "TELLO-XXXXXX"), using
 * Android's WifiNetworkSpecifier API (available since API 29 / Android 10).
 *
 * This exists as a small compiled Java class - rather than being done from
 * C# via Unity's AndroidJavaObject/AndroidJavaProxy reflection, like the
 * rest of this project's Android interop - specifically because
 * ConnectivityManager.NetworkCallback (the object that receives
 * onAvailable/onLost/onUnavailable) is a concrete Java class, not an
 * interface. AndroidJavaProxy only works for interfaces (it's built on
 * Java's dynamic Proxy mechanism, which can't subclass a concrete class),
 * so there was no way to implement this callback purely from C#.
 *
 * DROP THIS FILE AT:
 *   Assets/Plugins/Android/src/main/java/com/tello4quest2/TelloWifiConnector.java
 * (create the folder structure if it doesn't exist yet). Unity compiles any
 * .java source found under Assets/Plugins/Android/src automatically as part
 * of the normal Gradle build - no separate AAR project or manual compile
 * step needed.
 *
 * Reports back to the calling Unity GameObject via UnitySendMessage:
 *   - "OnTelloWifiConnected" with "1" (connected) or "0" (failed/timed out)
 *   - "OnTelloWifiLost" if the connection drops after being established
 *
 * Note: WifiNetworkSpecifier still shows the user a one-time system
 * confirmation dialog the first time an app requests a given network - this
 * is Android's own design (apps can't silently join arbitrary Wi-Fi networks
 * without at least one system-mediated confirmation), not something this
 * code can bypass. After that first approval, subsequent connections are
 * typically seamless.
 */
public class TelloWifiConnector {

    // Kept as a static field so it isn't garbage-collected while the async
    // connection request is still pending - ConnectivityManager only holds a
    // weak-ish reference internally in some Android versions.
    private static ConnectivityManager.NetworkCallback activeCallback;

    public static void connect(final String ssidPrefix, final String unityGameObjectName) {
        try {
            Context context = UnityPlayer.currentActivity;
            final ConnectivityManager connectivityManager =
                (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);

            WifiNetworkSpecifier specifier = new WifiNetworkSpecifier.Builder()
                .setSsidPattern(new PatternMatcher(ssidPrefix, PatternMatcher.PATTERN_PREFIX))
                .build();

            NetworkRequest request = new NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                // The Tello's hotspot has no internet access - without removing
                // this capability, Android may treat the network as undesirable
                // and avoid/deprioritize it in favor of a network that does have
                // internet (e.g. a saved home Wi-Fi), which isn't what we want.
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .setNetworkSpecifier(specifier)
                .build();

            activeCallback = new ConnectivityManager.NetworkCallback() {
                @Override
                public void onAvailable(Network network) {
                    super.onAvailable(network);
                    // Binds THIS APP's network traffic to the Tello's Wi-Fi
                    // specifically - without this, the OS might still route our
                    // UDP packets over a different active network (e.g. if the
                    // headset is also associated with another Wi-Fi/cellular
                    // path), even though this network is now connected.
                    connectivityManager.bindProcessToNetwork(network);
                    UnityPlayer.UnitySendMessage(unityGameObjectName, "OnTelloWifiConnected", "1");
                }

                @Override
                public void onUnavailable() {
                    super.onUnavailable();
                    UnityPlayer.UnitySendMessage(unityGameObjectName, "OnTelloWifiConnected", "0");
                }

                @Override
                public void onLost(Network network) {
                    super.onLost(network);
                    UnityPlayer.UnitySendMessage(unityGameObjectName, "OnTelloWifiLost", "");
                }
            };

            connectivityManager.requestNetwork(request, activeCallback);

        } catch (Exception e) {
            UnityPlayer.UnitySendMessage(unityGameObjectName, "OnTelloWifiConnected", "0");
        }
    }
}
