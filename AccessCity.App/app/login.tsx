import { View, Text, TextInput, TouchableOpacity, StyleSheet, Image } from "react-native";
import { router } from "expo-router";

export default function LoginScreen() {
  return (
    <View style={styles.container}>
      <View style={styles.topShape} />

      <Image
        source={require("../assets/images/logo.png")}
        style={styles.logo}
        resizeMode="contain"
      />

      <Text style={styles.title}>
        Navigate Your City <Text style={styles.orange}>Safely</Text>
      </Text>

      <Text style={styles.subtitle}>
        Find safer walking routes and stay aware{"\n"}of hazards around you.
      </Text>

      <View style={styles.switchContainer}>
        <TouchableOpacity style={styles.activeTab}>
          <Text style={styles.activeTabText}>Log In</Text>
        </TouchableOpacity>

        <TouchableOpacity style={styles.inactiveTab} onPress={() => router.push("/signup")}>
          <Text style={styles.inactiveTabText}>Sign up</Text>
        </TouchableOpacity>
      </View>

      <View style={styles.dividerRow}>
        <View style={styles.divider} />
        <Text style={styles.dividerText}>Login with</Text>
        <View style={styles.divider} />
      </View>

      <View style={styles.socialRow}>
        <TouchableOpacity style={styles.socialButton}>
          <Text>Google</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.socialButton}>
          <Text>Facebook</Text>
        </TouchableOpacity>
        <TouchableOpacity style={styles.socialButton}>
          <Text>Apple</Text>
        </TouchableOpacity>
      </View>

      <View style={styles.dividerRow}>
        <View style={styles.divider} />
        <Text style={styles.dividerText}>Or</Text>
        <View style={styles.divider} />
      </View>

      <Text style={styles.label}>Email</Text>
      <TextInput
        placeholder="Please enter your email"
        style={styles.input}
        accessibilityLabel="Email input"
      />

      <Text style={styles.label}>Password</Text>
      <TextInput
        placeholder="**********"
        secureTextEntry
        style={styles.input}
        accessibilityLabel="Password input"
      />

      <TouchableOpacity>
        <Text style={styles.forgotPassword}>Forgot Password?</Text>
      </TouchableOpacity>

      <TouchableOpacity style={styles.mainButton}>
        <Text style={styles.mainButtonText}>Log In</Text>
      </TouchableOpacity>
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: "#F7F7F7",
    paddingHorizontal: 24,
    paddingTop: 50,
  },
  topShape: {
    position: "absolute",
    top: 0,
    left: 0,
    width: 180,
    height: 180,
    backgroundColor: "#F1E2D7",
    borderBottomRightRadius: 180,
  },
  logo: {
    width: 120,
    height: 120,
    alignSelf: "center",
    marginTop: 30,
    marginBottom: 10,
  },
  title: {
    fontSize: 22,
    fontWeight: "800",
    textAlign: "center",
    color: "#4D3F3F",
    marginBottom: 8,
  },
  orange: {
    color: "#F28C28",
  },
  subtitle: {
    textAlign: "center",
    color: "#5F5F5F",
    fontSize: 15,
    marginBottom: 24,
  },
  switchContainer: {
    flexDirection: "row",
    backgroundColor: "#ECECF1",
    borderRadius: 12,
    padding: 4,
    marginBottom: 24,
  },
  activeTab: {
    flex: 1,
    backgroundColor: "#FFFFFF",
    paddingVertical: 12,
    borderRadius: 10,
    alignItems: "center",
  },
  inactiveTab: {
    flex: 1,
    paddingVertical: 12,
    borderRadius: 10,
    alignItems: "center",
  },
  activeTabText: {
    fontWeight: "700",
    color: "#1D2852",
  },
  inactiveTabText: {
    color: "#6D7280",
  },
  dividerRow: {
    flexDirection: "row",
    alignItems: "center",
    marginBottom: 18,
    marginTop: 6,
  },
  divider: {
    flex: 1,
    height: 1,
    backgroundColor: "#D8D8D8",
  },
  dividerText: {
    marginHorizontal: 12,
    color: "#6D7280",
    fontWeight: "600",
  },
  socialRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    marginBottom: 22,
  },
  socialButton: {
    flex: 1,
    backgroundColor: "#FFFFFF",
    paddingVertical: 12,
    borderRadius: 10,
    alignItems: "center",
    marginHorizontal: 4,
    borderWidth: 1,
    borderColor: "#E2E2E2",
    minHeight: 44,
  },
  label: {
    fontSize: 15,
    fontWeight: "600",
    color: "#6D7280",
    marginBottom: 8,
    marginLeft: 4,
  },
  input: {
    backgroundColor: "#FFFFFF",
    borderWidth: 1,
    borderColor: "#D9D9D9",
    borderRadius: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
    marginBottom: 18,
    minHeight: 50,
  },
  forgotPassword: {
    textAlign: "right",
    color: "#1D4E9E",
    fontWeight: "600",
    marginBottom: 24,
  },
  mainButton: {
    backgroundColor: "#215A9A",
    paddingVertical: 16,
    borderRadius: 14,
    alignItems: "center",
    minHeight: 50,
    shadowColor: "#000",
    shadowOpacity: 0.15,
    shadowRadius: 5,
    elevation: 4,
  },
  mainButtonText: {
    color: "#FFFFFF",
    fontSize: 20,
    fontWeight: "700",
  },
});